using System.Diagnostics;

using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>The git monitor against a real repository made in a temporary folder; skipped when git is not installed.</summary>
public sealed class GitRepositoryTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "PulseTests", "git-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
			{
				File.SetAttributes(file, FileAttributes.Normal); // git makes its objects read-only.
			}

			Directory.Delete(_folder, recursive: true);
		}
	}

	[Fact]
	public void ParseStatus_CountsBranchUpstreamAndChanges()
	{
		// Arrange
		const string output = "# branch.oid abc\n# branch.head feature/x\n# branch.upstream origin/feature/x\n# branch.ab +2 -1\n"
							  + "1 .M N... 100644 100644 100644 a b src/a.cs\n2 R. N... 100644 100644 100644 a b R100 new.cs\told.cs\n? notes.txt\n! bin/\n";

		// Act
		var (branch, ahead, behind, changed) = GitRepository.ParseStatus(output);

		// Assert
		Assert.Equal("feature/x", branch);
		Assert.Equal(2, ahead);
		Assert.Equal(1, behind);
		Assert.Equal(3, changed);
	}

	[Fact]
	public void ReadHeadCommit_BranchFileOrPackedRefsOrDetached()
	{
		// Arrange: a git directory as git leaves it, written by hand.
		var git = Path.Combine(_folder, ".git");
		Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
		File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), new string('a', 40) + "\n");
		File.WriteAllText(Path.Combine(git, "packed-refs"), $"# pack-refs with: peeled fully-peeled sorted\n{new string('b', 40)} refs/heads/release\n");
		File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
		var repository = GitRepository.TryOpen(_folder)!;

		// Act
		var main = repository.ReadHeadCommit();
		File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/release\n");
		var release = repository.ReadHeadCommit();
		File.WriteAllText(Path.Combine(git, "HEAD"), new string('c', 40) + "\n");
		var detached = repository.ReadHeadCommit();
		File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/new-branch\n");
		var unborn = repository.ReadHeadCommit();

		// Assert
		Assert.Equal(new string('a', 40), main);
		Assert.Equal(new string('b', 40), release);
		Assert.Equal(new string('c', 40), detached);
		Assert.Null(unborn);
	}

	[Fact]
	public void ParseNumstat_AddsUpTextFiles_AndSkipsBinaries()
	{
		// Act
		var (insertions, deletions) = GitRepository.ParseNumstat("10\t2\tsrc/a.cs\n-\t-\timage.png\n3\t0\tREADME.md\n");

		// Assert
		Assert.Equal((13, 2), (insertions, deletions));
	}

	[Fact]
	public async Task Repository_CommitAndChange_ShowUpAsEventAndStatus()
	{
		Assert.SkipUnless(Git("--version") is not null, "git is not installed.");

		// Arrange: a repository with one commit, followed from its start.
		Directory.CreateDirectory(_folder);
		Git("init", "-q", "-b", "main", _folder);
		Git("-C", _folder, "config", "user.email", "test@example.org");
		Git("-C", _folder, "config", "user.name", "Pulse Test");
		File.WriteAllText(Path.Combine(_folder, "a.txt"), "one\n");
		Git("-C", _folder, "add", "a.txt");
		Git("-C", _folder, "commit", "-q", "-m", "First");
		var monitor = new GitMonitor(GitMonitor.Find([_folder]));
		var events = new List<PulseEvent>();
		monitor.Poll(100, TimeProvider.System.GetUtcNow(), events, []);

		// Act: a second commit, then a change that is not committed.
		File.WriteAllText(Path.Combine(_folder, "a.txt"), "one\ntwo\n");
		Git("-C", _folder, "commit", "-q", "-am", "Second");
		File.WriteAllText(Path.Combine(_folder, "a.txt"), "one\ntwo\nthree\nfour\n");
		events.Clear();
		monitor.Poll(101, TimeProvider.System.GetUtcNow(), events, []);
		GitActivity activity;
		var waited = Stopwatch.StartNew();
		do
		{
			await Task.Delay(200, TestContext.Current.CancellationToken);
			activity = monitor.Poll(200 + waited.Elapsed.TotalSeconds, TimeProvider.System.GetUtcNow(), [], []);
		}
		while (activity.Repositories[0] is not { StatusKnown: true, ChangedFiles: > 0 } && waited.Elapsed < TimeSpan.FromSeconds(20));

		// Assert
		var commit = Assert.Single(events);
		Assert.Equal(("commit", "Second", EventKind.Git), (commit.Action, commit.Text, commit.Kind));
		var status = activity.Repositories[0];
		Assert.Equal("main", status.Branch);
		Assert.Equal((1, 2, 0), (status.ChangedFiles, status.Insertions, status.Deletions));
		Assert.Equal(2, activity.ChangedLines);
		Assert.NotNull(status.LastCommit);
		Assert.Null(status.State);
	}

	[Fact]
	public void Find_FolderOfRepositories_FindsEachOne()
	{
		Assert.SkipUnless(Git("--version") is not null, "git is not installed.");

		// Arrange
		foreach (var name in new[] { "one", "two" })
		{
			Directory.CreateDirectory(Path.Combine(_folder, name));
			Git("init", "-q", Path.Combine(_folder, name));
		}

		Directory.CreateDirectory(Path.Combine(_folder, "plain"));

		// Act
		var found = GitRepository.Find(_folder);

		// Assert
		Assert.Equal(["one", "two"], found.Select(repository => repository.Name));
	}

	private static string? Git(params string[] arguments)
	{
		try
		{
			var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
			foreach (var argument in arguments)
			{
				start.ArgumentList.Add(argument);
			}

			using var process = Process.Start(start)!;
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			return process.ExitCode == 0 ? output : null;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}
}
