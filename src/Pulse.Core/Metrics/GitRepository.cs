using System.Diagnostics;
using System.Globalization;
using System.Text;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Metrics;

/// <summary>
/// One git repository Pulse follows. Its events come from the reflogs of HEAD (the main working tree and every linked
/// worktree), read only where they grew, so whoever moves HEAD (a shell, an IDE, an agent) shows up without Pulse
/// running git. git itself is asked, in the background, for the branch, the changes that are not committed yet and the
/// distance to the upstream: every 5 seconds while the repository is active (HEAD moved, or a process works in it,
/// within the last 10 minutes), every minute while it only has changes, and not at all while it rests. It runs with
/// <c>--no-optional-locks</c>, so Pulse never takes the index lock another git needs.
/// </summary>
internal sealed class GitRepository
{
	/// <summary>A repository counts as active this long after HEAD moved or a process worked in it.</summary>
	public const double ActiveSeconds = 600;

	private const double RefreshSeconds = 5;
	private const double SlowRefreshSeconds = 30;
	private const double DirtyRefreshSeconds = 60;
	private const double SlowGitSeconds = 3;
	private const double WorktreeScanSeconds = 15;
	private const int TailBytes = 64 * 1024;
	private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);
	private static volatile bool _gitMissing;

	/// <summary>At most two repositories ask git at a time, so finding dozens of repositories does not load the machine.</summary>
	private static readonly SemaphoreSlim Concurrency = new(2);

	private readonly Dictionary<string, Reflog> _reflogs = new(StringComparer.OrdinalIgnoreCase);
	private readonly Lock _gate = new();
	private GitRepositoryStatus _status;
	private Task? _refresh;
	private double _lastRefresh = double.NegativeInfinity;
	private double _nextScan;
	private bool _scanned;
	private volatile bool _slow;
	private bool _moved;
	private double _lastActive = double.NegativeInfinity;
	private DateTimeOffset? _lastActivity;
	private DateTimeOffset? _lastCommit;

	private GitRepository(string root, string gitDirectory, string commonDirectory)
	{
		Root = root.TrimEnd('\\');
		Name = Path.GetFileName(Root) is { Length: > 0 } name ? name : Root;
		GitDirectory = gitDirectory;
		CommonDirectory = commonDirectory;
		_lastCommit = ReadLastCommit(Path.Combine(gitDirectory, "logs", "HEAD"));
		_lastActivity = _lastCommit;
		_status = new GitRepositoryStatus(Name, Root, ReadHeadBranch(), ReadState(), 0, 0, 0, null, null, ReadLockAge(), _lastCommit, StatusKnown: false);
	}

	public string Name { get; }

	public string Root { get; }

	public string GitDirectory { get; }

	public string CommonDirectory { get; }

	/// <summary>Named on the command line: always active, so its working tree is always asked for.</summary>
	public bool AlwaysActive { get; init; }

	/// <summary>The repository whose working tree holds <paramref name="path"/>, looking upward; null when there is none.</summary>
	public static GitRepository? TryOpen(string path, bool alwaysActive = false)
	{
		for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
		{
			var dotGit = Path.Combine(directory.FullName, ".git");
			if (Directory.Exists(dotGit))
			{
				return new GitRepository(directory.FullName, dotGit, dotGit) { AlwaysActive = alwaysActive };
			}

			if (File.Exists(dotGit))
			{
				// A linked worktree or a submodule: ".git" is a file that points at its git directory.
				var line = File.ReadLines(dotGit).FirstOrDefault() ?? string.Empty;
				if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
				{
					return null;
				}

				var gitDirectory = Path.GetFullPath(Path.Combine(directory.FullName, line[7..].Trim()));
				var commonFile = Path.Combine(gitDirectory, "commondir");
				var common = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(commonFile).Trim())) : gitDirectory;
				return new GitRepository(directory.FullName, gitDirectory, common) { AlwaysActive = alwaysActive };
			}
		}

		return null;
	}

	/// <summary>
	/// The repositories of one <c>-git</c> folder: the repository around the folder, or else every repository directly
	/// inside it (<c>-git D:\Repos</c>). They are always active.
	/// </summary>
	public static IReadOnlyList<GitRepository> Find(string path)
	{
		var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
		if (TryOpen(full, alwaysActive: true) is { } repository)
		{
			return [repository];
		}

		if (!Directory.Exists(full))
		{
			return [];
		}

		var found = new List<GitRepository>();
		foreach (var child in Directory.EnumerateDirectories(full).Order(StringComparer.OrdinalIgnoreCase))
		{
			if (GitDiscovery.IsWorkingTree(child) && TryOpen(child, alwaysActive: true) is { } inside)
			{
				found.Add(inside);
			}
		}

		return found;
	}

	/// <summary>Marks the repository as in use: a process works in it.</summary>
	public void MarkActive(double now, DateTimeOffset wallNow)
	{
		_lastActive = now;
		lock (_gate)
		{
			_lastActivity = wallNow;
		}
	}

	public bool IsActive(double now) => AlwaysActive || now - _lastActive <= ActiveSeconds;

	/// <summary>The latest state; the working-tree part is refreshed in the background.</summary>
	public GitRepositoryStatus StatusAt(double now)
	{
		lock (_gate)
		{
			return _status with { Active = IsActive(now), LastActivity = _lastActivity };
		}
	}

	/// <summary>
	/// The reflog lines written since the previous call, of HEAD in the main working tree and every worktree. A reflog
	/// that another repository already follows (<paramref name="claimed"/>) is left to it, so an event counts once.
	/// </summary>
	public void ReadEvents(List<(ReflogEntry Entry, string Source)> into, double now, ISet<string> claimed)
	{
		if (now >= _nextScan)
		{
			ScanReflogs(claimed);
			_nextScan = now + WorktreeScanSeconds;
		}

		foreach (var (path, reflog) in _reflogs)
		{
			long length;
			try
			{
				length = new FileInfo(path).Length;
			}
			catch (IOException)
			{
				continue;
			}

			if (length < reflog.Position)
			{
				reflog.Position = length; // Expired or rewritten by git gc: start over from here.
			}

			if (length > reflog.Position && ReadAppended(path, reflog, length, into))
			{
				_moved = true;
				_lastActive = now;
			}
		}
	}

	/// <summary>
	/// Asks git for the working tree in the background when it is time: at once after HEAD moved, every few seconds
	/// while active, every minute while it has changes; at most one question at a time.
	/// </summary>
	public void RefreshIfDue(double now)
	{
		if (_refresh is { IsCompleted: false })
		{
			return;
		}

		var interval = IsActive(now) ? (_slow ? SlowRefreshSeconds : RefreshSeconds)
			: StatusAt(now).ChangedFiles > 0 ? DirtyRefreshSeconds
			: double.PositiveInfinity;
		if (!_moved && now - _lastRefresh < interval)
		{
			return;
		}

		_moved = false;
		_lastRefresh = now;
		_refresh = Task.Run(RefreshAsync);
	}

	/// <summary>Counts what <c>git status --porcelain=v2 --branch</c> says: branch, ahead and behind, changed files.</summary>
	internal static (string? Branch, int? Ahead, int? Behind, int Changed) ParseStatus(string output)
	{
		string? branch = null;
		int? ahead = null, behind = null;
		var changed = 0;
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
			{
				branch = line["# branch.head ".Length..];
			}
			else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
			{
				var parts = line["# branch.ab ".Length..].Split(' ');
				if (parts.Length == 2
					&& int.TryParse(parts[0].TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
					&& int.TryParse(parts[1].TrimStart('-'), NumberStyles.None, CultureInfo.InvariantCulture, out var b))
				{
					(ahead, behind) = (a, b);
				}
			}
			else if (line.Length > 1 && line[1] == ' ' && line[0] is '1' or '2' or 'u' or '?')
			{
				changed++;
			}
		}

		return (branch, ahead, behind, changed);
	}

	/// <summary>Adds up <c>git diff --numstat</c>: "added TAB removed TAB path" per file; binary files show "-".</summary>
	internal static (int Insertions, int Deletions) ParseNumstat(string output)
	{
		int insertions = 0, deletions = 0;
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = line.Split('\t');
			if (parts.Length >= 3
				&& int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added)
				&& int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var removed))
			{
				insertions += added;
				deletions += removed;
			}
		}

		return (insertions, deletions);
	}

	private static DateTimeOffset? ReadLastCommit(string reflog)
	{
		try
		{
			using var stream = new FileStream(reflog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			DateTimeOffset? last = null;
			while (reader.ReadLine() is { } line)
			{
				if (ReflogEntry.TryParse(line, out var entry) && entry.Action is "commit" or "amend" or "merge")
				{
					last = entry.At;
				}
			}

			return last;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Finds the HEAD reflogs to follow; ones found at start are read from their end, later ones from the start.</summary>
	private void ScanReflogs(ISet<string> claimed)
	{
		Follow(Path.Combine(GitDirectory, "logs", "HEAD"), Name);
		var worktrees = Path.Combine(CommonDirectory, "worktrees");
		if (string.Equals(GitDirectory, CommonDirectory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(worktrees))
		{
			foreach (var worktree in Directory.EnumerateDirectories(worktrees))
			{
				Follow(Path.Combine(worktree, "logs", "HEAD"), $"{Name}/{Path.GetFileName(worktree)}");
			}
		}

		_scanned = true;

		void Follow(string path, string label)
		{
			if (!_reflogs.ContainsKey(path) && File.Exists(path) && claimed.Add(path))
			{
				_reflogs[path] = new Reflog(label) { Position = _scanned ? 0 : new FileInfo(path).Length };
			}
		}
	}

	/// <summary>Reads the whole lines appended since the last read; true when one of them was an event.</summary>
	private bool ReadAppended(string path, Reflog reflog, long length, List<(ReflogEntry Entry, string Source)> into)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			stream.Seek(reflog.Position, SeekOrigin.Begin);
			var buffer = new byte[length - reflog.Position];
			stream.ReadExactly(buffer);

			// Only whole lines; a line still being written is read next time.
			var end = Array.LastIndexOf(buffer, (byte) '\n');
			if (end < 0)
			{
				return false;
			}

			reflog.Position += end + 1;
			var found = false;
			foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
			{
				if (!ReflogEntry.TryParse(line.TrimEnd('\r'), out var entry))
				{
					continue;
				}

				into.Add((entry, reflog.Label));
				found = true;
				lock (_gate)
				{
					_lastActivity = entry.At;
					if (entry.Action is "commit" or "amend" or "merge")
					{
						_lastCommit = entry.At;
					}
				}
			}

			return found;
		}
		catch (IOException ex)
		{
			Log.WarnOnce($"git-reflog-{path}", $"Could not read the reflog {path}.", ex);
			return false;
		}
	}

	private async Task RefreshAsync()
	{
		await Concurrency.WaitAsync().ConfigureAwait(false);
		try
		{
			await AskGitAsync().ConfigureAwait(false);
		}
		finally
		{
			Concurrency.Release();
		}
	}

	private async Task AskGitAsync()
	{
		var started = Stopwatch.GetTimestamp();
		var branch = ReadHeadBranch();
		var state = ReadState();
		var lockAge = ReadLockAge();
		int? ahead = null, behind = null;
		int changed = 0, insertions = 0, deletions = 0;
		var known = false;
		if (!_gitMissing)
		{
			var status = await RunGitAsync("status", "--porcelain=v2", "--branch", "--untracked-files=normal").ConfigureAwait(false);
			if (status is not null)
			{
				(var gitBranch, ahead, behind, changed) = ParseStatus(status);
				branch = gitBranch ?? branch;
				var numstat = await RunGitAsync("diff", "HEAD", "--numstat").ConfigureAwait(false);
				(insertions, deletions) = numstat is null ? (0, 0) : ParseNumstat(numstat);
				known = true;
			}
		}

		_slow = Stopwatch.GetElapsedTime(started).TotalSeconds > SlowGitSeconds;
		lock (_gate)
		{
			_status = new GitRepositoryStatus(Name, Root, branch, state, changed, insertions, deletions, ahead, behind, lockAge, _lastCommit, known);
		}
	}

	/// <summary>Runs git in the working tree; null when git is missing, fails or takes too long.</summary>
	private async Task<string?> RunGitAsync(params string[] arguments)
	{
		var start = new ProcessStartInfo("git")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
		};
		start.ArgumentList.Add("--no-optional-locks");
		start.ArgumentList.Add("-C");
		start.ArgumentList.Add(Root);
		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		try
		{
			using var process = Process.Start(start);
			if (process is null)
			{
				return null;
			}

			using var timeout = new CancellationTokenSource(GitTimeout);
			var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
			_ = process.StandardError.ReadToEndAsync(timeout.Token);
			try
			{
				await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				process.Kill(entireProcessTree: true);
				Log.WarnOnce($"git-timeout-{Root}", $"git {arguments[0]} in {Root} took longer than {GitTimeout.TotalSeconds} s; it was stopped.");
				return null;
			}

			return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			_gitMissing = true;
			Log.WarnOnce("git-missing", "git was not found on the PATH; only the reflog and the repository state are followed.", ex);
			return null;
		}
	}

	/// <summary>
	/// The commit checked out, read from the files without running git: HEAD itself when it is detached, else the branch
	/// it names, from its own file or from packed-refs; null when it cannot be read (a branch without commits).
	/// </summary>
	public string? ReadHeadCommit()
	{
		try
		{
			var head = File.ReadAllText(Path.Combine(GitDirectory, "HEAD")).Trim();
			if (!head.StartsWith("ref: ", StringComparison.Ordinal))
			{
				return IsCommitId(head) ? head : null;
			}

			var name = head[5..].Trim();
			foreach (var directory in new[] { GitDirectory, CommonDirectory })
			{
				var file = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
				if (File.Exists(file) && File.ReadAllText(file).Trim() is var id && IsCommitId(id))
				{
					return id;
				}
			}

			var packed = Path.Combine(CommonDirectory, "packed-refs");
			return File.Exists(packed)
				? File.ReadLines(packed).Select(line => line.Split(' ', 2)).FirstOrDefault(parts => parts.Length == 2 && parts[1] == name && IsCommitId(parts[0]))?[0]
				: null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		static bool IsCommitId(string text) => text.Length is 40 or 64 && text.All(char.IsAsciiHexDigit);
	}

	private string? ReadHeadBranch()
	{
		try
		{
			var head = File.ReadAllText(Path.Combine(GitDirectory, "HEAD")).Trim();
			return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head["ref: refs/heads/".Length..] : "(detached)";
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	private string? ReadState()
	{
		if (File.Exists(Path.Combine(GitDirectory, "MERGE_HEAD")))
		{
			return "merging";
		}

		if (Directory.Exists(Path.Combine(GitDirectory, "rebase-merge")) || Directory.Exists(Path.Combine(GitDirectory, "rebase-apply")))
		{
			return "rebasing";
		}

		if (File.Exists(Path.Combine(GitDirectory, "CHERRY_PICK_HEAD")))
		{
			return "cherry-picking";
		}

		if (File.Exists(Path.Combine(GitDirectory, "REVERT_HEAD")))
		{
			return "reverting";
		}

		return File.Exists(Path.Combine(GitDirectory, "BISECT_LOG")) ? "bisecting" : null;
	}

	private TimeSpan? ReadLockAge()
	{
		var lockFile = new FileInfo(Path.Combine(GitDirectory, "index.lock"));
		return lockFile.Exists ? TimeProvider.System.GetUtcNow() - new DateTimeOffset(lockFile.LastWriteTimeUtc, TimeSpan.Zero) : null;
	}

	/// <summary>A followed reflog: how far it was read, and what its events are called.</summary>
	private sealed class Reflog(string label)
	{
		public string Label { get; } = label;

		public long Position { get; set; }
	}
}
