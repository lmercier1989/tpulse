using System.Collections.Concurrent;
using System.Text.Json;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Metrics;

/// <summary>Token counts of one or more model calls.</summary>
public readonly record struct TokenCounts(long Input, long CacheWrite, long CacheRead, long Output)
{
	/// <summary>Input the model had to process fresh: uncached input plus input written to the cache.</summary>
	public long FreshInput => Input + CacheWrite;

	/// <summary>Everything the model read: fresh input plus cache reads.</summary>
	public long AllInput => Input + CacheWrite + CacheRead;

	public static TokenCounts operator +(TokenCounts a, TokenCounts b)
		=> new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
}

/// <summary>A Claude Code session as seen in its transcript.</summary>
/// <param name="SessionId">The session id.</param>
/// <param name="Project">Name of the folder the session works in.</param>
/// <param name="Branch">Git branch, when there is one.</param>
/// <param name="Model">Model of the latest answer, for example "claude-opus-5-5".</param>
/// <param name="ContextTokens">Tokens the latest main-thread call read: how full the context is.</param>
/// <param name="LastActivity">Time of the latest answer.</param>
/// <param name="Tokens">Tokens counted since Pulse started.</param>
public sealed record ClaudeSession(string SessionId, string Project, string? Branch, string Model, long ContextTokens, DateTimeOffset LastActivity, TokenCounts Tokens);

/// <summary>
/// Follows the Claude Code transcripts (<c>~/.claude/projects/*/*.jsonl</c>, written by both the desktop app and the
/// command line) and reports the tokens of every new answer and the sessions that are active. Only the usage numbers,
/// model, time, folder and branch of answers are read; the conversation itself is never parsed or kept.
/// <para>
/// Claude Code writes one line per content block of an answer, each carrying the usage of the whole answer, so usage
/// is counted per message id and only its growth is added. Files are read from where the previous read stopped;
/// files that were already there when Pulse started, or that it sees for the first time, are read for their latest
/// state only, so old answers are never counted as new.
/// </para>
/// </summary>
internal sealed class ClaudeTranscripts : IDisposable
{
	private const int TailBytes = 256 * 1024;
	private const int MaximumReadBytes = 8 * 1024 * 1024;
	private const int RememberedMessages = 4096;
	private const double RescanSeconds = 15;
	private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan StartupWindow = TimeSpan.FromHours(1);

	private readonly string _root;
	private readonly FileSystemWatcher? _watcher;
	private readonly ConcurrentDictionary<string, byte> _changed = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, TokenCounts> _counted = [];
	private readonly Queue<string> _countedOrder = new();
	private readonly Dictionary<string, SessionState> _sessions = [];
	private TokenCounts _pending;
	private TokenCounts _total;
	private DateTimeOffset _lastScan;
	private double _nextRescan;

	internal ClaudeTranscripts(string root, bool watch = true)
	{
		_root = root;
		_lastScan = TimeProvider.System.GetUtcNow();
		foreach (var file in RecentFiles(StartupWindow))
		{
			ReadTail(file);
		}

		if (!watch)
		{
			return;
		}

		try
		{
			_watcher = new FileSystemWatcher(root, "*.jsonl")
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
				InternalBufferSize = 64 * 1024,
			};
			_watcher.Changed += (_, e) => _changed[e.FullPath] = 0;
			_watcher.Created += (_, e) => _changed[e.FullPath] = 0;
			_watcher.EnableRaisingEvents = true;
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
		{
			Log.Warn("Cannot watch the Claude Code transcripts; they are scanned every 15 seconds instead.", ex);
		}
	}

	/// <summary>Tokens counted since Pulse started.</summary>
	public TokenCounts Total => _total;

	/// <summary>The transcript folder: <c>%CLAUDE_CONFIG_DIR%\projects</c> or <c>%USERPROFILE%\.claude\projects</c>.</summary>
	public static ClaudeTranscripts? TryCreate()
	{
		var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
		var root = string.IsNullOrWhiteSpace(configured)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects")
			: Path.Combine(configured, "projects");
		if (!Directory.Exists(root))
		{
			Log.Info($"No Claude Code transcripts at {root}; token graphs stay empty.");
			return null;
		}

		Log.Info($"Following Claude Code transcripts in {root}.");
		return new ClaudeTranscripts(root);
	}

	/// <summary>The tokens of answers written since the previous call, and the sessions active in the last 10 minutes.</summary>
	public (TokenCounts Tokens, IReadOnlyList<ClaudeSession> Sessions) Poll(double now)
	{
		if (_watcher is null || now >= _nextRescan)
		{
			// A safety net for events the watcher dropped, and the only source when it could not start.
			_nextRescan = now + RescanSeconds;
			var since = _lastScan - TimeSpan.FromSeconds(RescanSeconds + 5);
			_lastScan = TimeProvider.System.GetUtcNow();
			foreach (var file in RecentFiles(TimeProvider.System.GetUtcNow() - since))
			{
				_changed[file] = 0;
			}
		}

		foreach (var path in _changed.Keys)
		{
			_changed.TryRemove(path, out _);
			ReadNew(path);
		}

		var tokens = _pending;
		_pending = default;
		var cutoff = TimeProvider.System.GetUtcNow() - ActiveWindow;
		var sessions = _sessions.Values
			.Where(session => session.LastActivity >= cutoff)
			.OrderByDescending(session => session.LastActivity)
			.Select(session => session.ToSession())
			.ToList();
		return (tokens, sessions);
	}

	public void Dispose() => _watcher?.Dispose();

	/// <summary>Reads one transcript line; counts its tokens when <paramref name="count"/>.</summary>
	internal void ReadLine(ReadOnlySpan<byte> line, bool count)
	{
		if (line.StartsWith("﻿"u8))
		{
			line = line[3..]; // A byte order mark at the start of the file.
		}

		if (line.IndexOf("\"usage\""u8) < 0)
		{
			return;
		}

		try
		{
			using var document = JsonDocument.Parse(line.ToArray());
			var root = document.RootElement;
			if (!Text(root, "type").Equals("assistant", StringComparison.Ordinal)
				|| !root.TryGetProperty("message", out var message)
				|| !message.TryGetProperty("usage", out var usage))
			{
				return;
			}

			var model = Text(message, "model");
			if (model.Length == 0 || model.StartsWith('<'))
			{
				return; // Synthetic messages (errors, interruptions) carry no real usage.
			}

			var counts = new TokenCounts(
				Number(usage, "input_tokens"),
				Number(usage, "cache_creation_input_tokens"),
				Number(usage, "cache_read_input_tokens"),
				Number(usage, "output_tokens"));
			var id = Text(message, "id");
			if (id.Length == 0)
			{
				id = Text(root, "requestId");
			}

			var delta = Remember(id, counts);
			if (count)
			{
				_pending += delta;
				_total += delta;
			}

			var sessionId = Text(root, "sessionId");
			if (sessionId.Length == 0)
			{
				return;
			}

			if (!_sessions.TryGetValue(sessionId, out var session))
			{
				session = new SessionState(sessionId);
				_sessions[sessionId] = session;
			}

			var cwd = Text(root, "cwd");
			session.Project = cwd.Length == 0 ? session.Project : Path.GetFileName(cwd.TrimEnd('\\', '/'));
			var branch = Text(root, "gitBranch");
			session.Branch = branch.Length == 0 || branch == "HEAD" ? session.Branch : branch;
			session.Model = model;
			if (!(root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True))
			{
				session.ContextTokens = counts.AllInput;
			}

			if (DateTimeOffset.TryParse(Text(root, "timestamp"), out var time) && time > session.LastActivity)
			{
				session.LastActivity = time;
			}

			if (count)
			{
				session.Tokens += delta;
			}
		}
		catch (JsonException)
		{
			// A line being written right now; it is read again once complete.
		}
	}

	private static string Text(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

	private static long Number(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

	/// <summary>Records the usage of a message; returns what it adds to what was counted for that message before.</summary>
	private TokenCounts Remember(string id, TokenCounts counts)
	{
		if (id.Length == 0)
		{
			return counts;
		}

		if (!_counted.TryGetValue(id, out var before))
		{
			_countedOrder.Enqueue(id);
			if (_countedOrder.Count > RememberedMessages)
			{
				_counted.Remove(_countedOrder.Dequeue());
			}
		}

		var now = new TokenCounts(
			Math.Max(before.Input, counts.Input),
			Math.Max(before.CacheWrite, counts.CacheWrite),
			Math.Max(before.CacheRead, counts.CacheRead),
			Math.Max(before.Output, counts.Output));
		_counted[id] = now;
		return new TokenCounts(now.Input - before.Input, now.CacheWrite - before.CacheWrite, now.CacheRead - before.CacheRead, now.Output - before.Output);
	}

	private List<string> RecentFiles(TimeSpan age)
	{
		var since = (TimeProvider.System.GetUtcNow() - age).UtcDateTime;
		try
		{
			return new DirectoryInfo(_root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
				.Where(file => file.LastWriteTimeUtc >= since)
				.Select(file => file.FullName)
				.ToList();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.WarnOnce("claude-scan", "Could not list the Claude Code transcripts.", ex);
			return [];
		}
	}

	/// <summary>Learns the latest state of a transcript without counting its tokens; later reads start at its end.</summary>
	private void ReadTail(string path)
	{
		try
		{
			using var stream = Open(path);
			var length = stream.Length;
			var start = Math.Max(0, length - TailBytes);
			stream.Position = start;
			var bytes = new byte[length - start];
			stream.ReadExactly(bytes);
			var span = bytes.AsSpan();
			if (start > 0)
			{
				var firstBreak = span.IndexOf((byte) '\n');
				span = firstBreak < 0 ? [] : span[(firstBreak + 1)..];
			}

			var consumed = ReadLines(span, count: false);
			_offsets[path] = start + (bytes.Length - span.Length) + consumed;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.WarnOnce($"claude-tail-{path}", $"Could not read the Claude Code transcript {path}.", ex);
		}
	}

	/// <summary>Reads what was appended to a transcript since the last read and counts its tokens.</summary>
	private void ReadNew(string path)
	{
		if (!_offsets.TryGetValue(path, out var offset))
		{
			ReadTail(path);
			return;
		}

		try
		{
			using var stream = Open(path);
			var length = stream.Length;
			if (length < offset)
			{
				_offsets[path] = length; // Rewritten: start over from its end.
				return;
			}

			if (length == offset)
			{
				return;
			}

			var size = (int) Math.Min(length - offset, MaximumReadBytes);
			var bytes = new byte[size];
			stream.Position = offset;
			stream.ReadExactly(bytes);
			_offsets[path] = offset + ReadLines(bytes, count: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.WarnOnce($"claude-read-{path}", $"Could not read the Claude Code transcript {path}.", ex);
		}
	}

	/// <summary>Reads complete lines; returns the bytes consumed, so a line still being written is read next time.</summary>
	private int ReadLines(ReadOnlySpan<byte> bytes, bool count)
	{
		var consumed = 0;
		while (true)
		{
			var end = bytes[consumed..].IndexOf((byte) '\n');
			if (end < 0)
			{
				return consumed;
			}

			ReadLine(bytes.Slice(consumed, end), count);
			consumed += end + 1;
		}
	}

	private static FileStream Open(string path)
		=> new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

	private sealed class SessionState(string id)
	{
		public string Project { get; set; } = string.Empty;

		public string? Branch { get; set; }

		public string Model { get; set; } = string.Empty;

		public long ContextTokens { get; set; }

		public DateTimeOffset LastActivity { get; set; }

		public TokenCounts Tokens { get; set; }

		public ClaudeSession ToSession() => new(id, Project, Branch, Model, ContextTokens, LastActivity, Tokens);
	}
}
