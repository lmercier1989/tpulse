namespace Pulse.Core.Metrics;

/// <summary>Where an event on the time axis comes from; each kind has its own marker colour.</summary>
public enum EventKind
{
	/// <summary>A commit, checkout, merge, rebase, reset or pull in a followed repository.</summary>
	Git,

	/// <summary>An error or critical event in the Windows System or Application log.</summary>
	Error,

	/// <summary>A marker put with <c>tpulse -mark</c>, or the start and end of a command measured with <c>tpulse -- command</c>.</summary>
	Mark,

	/// <summary>A graph that jumped well above its own recent level; drawn on that graph only.</summary>
	Spike,

	/// <summary>A process whose memory kept growing for ten minutes; drawn on the memory graph only.</summary>
	Leak,
}

/// <summary>
/// Something that happened at one moment, drawn as a marker across all graphs (a spike or a leak on its own graph only)
/// and listed in the info panel.
/// </summary>
/// <param name="Time">The moment on the sample clock (<see cref="Diagnostics.MonotonicClock"/>), where the marker is drawn.</param>
/// <param name="At">The wall-clock time it happened.</param>
/// <param name="Kind">Git, a Windows error, a mark, a spike or a leak.</param>
/// <param name="Source">The repository, the provider that logged the error, who put the mark, or the graph of a spike or leak.</param>
/// <param name="Action">"commit", "checkout", "merge", ...; "error" or "critical" for the Windows log; "mark", "run", "done" or
/// "failed" for a mark; "spike"; "leak".</param>
/// <param name="Text">The commit message, the branch, the first line of the error, the text of the mark, or what was found.</param>
public sealed record PulseEvent(double Time, DateTimeOffset At, EventKind Kind, string Source, string Action, string Text)
{
	/// <summary>Whether the marker belongs on the graph titled <paramref name="graphTitle"/>: a spike or a leak on its own graph only.</summary>
	public bool BelongsTo(string graphTitle) => Kind is not (EventKind.Spike or EventKind.Leak) || string.Equals(Source, graphTitle, StringComparison.Ordinal);

	/// <summary>The kind of an event from its action, for events read back from a recording.</summary>
	public static EventKind KindOf(string action) => action switch
	{
		"error" or "critical" => EventKind.Error,
		"mark" or "run" or "done" or "failed" => EventKind.Mark,
		"spike" => EventKind.Spike,
		"leak" => EventKind.Leak,
		_ => EventKind.Git,
	};
}

/// <summary>A process name with its load: CPU in % of the machine, or I/O in bytes per second.</summary>
/// <param name="Name">The image name without ".exe".</param>
/// <param name="Count">How many processes of that name were added up.</param>
/// <param name="Value">CPU % or bytes per second.</param>
public sealed record ProcessLoad(string Name, int Count, double Value);

/// <summary>The state of one followed git repository.</summary>
/// <param name="Name">The folder name of its working tree.</param>
/// <param name="Root">The working tree.</param>
/// <param name="Branch">The checked-out branch, or "(detached)".</param>
/// <param name="State">"merging", "rebasing", "cherry-picking", "reverting" or "bisecting"; null when none is in progress.</param>
/// <param name="ChangedFiles">Changed and untracked files.</param>
/// <param name="Insertions">Lines added and not committed yet.</param>
/// <param name="Deletions">Lines removed and not committed yet.</param>
/// <param name="Ahead">Commits the branch is ahead of its upstream; null without an upstream.</param>
/// <param name="Behind">Commits the branch is behind its upstream, as far as the last fetch knows.</param>
/// <param name="IndexLockAge">How long index.lock has existed; null when there is none.</param>
/// <param name="LastCommit">When HEAD last got a commit, from the reflog.</param>
/// <param name="StatusKnown">Whether git could be asked for the working tree (false without git, or before the first answer).</param>
/// <param name="Active">HEAD moved or a process worked in it lately, or it was named with <c>-git</c>.</param>
/// <param name="LastActivity">When HEAD last moved or a process was last seen working in it.</param>
public sealed record GitRepositoryStatus(
	string Name,
	string Root,
	string? Branch,
	string? State,
	int ChangedFiles,
	int Insertions,
	int Deletions,
	int? Ahead,
	int? Behind,
	TimeSpan? IndexLockAge,
	DateTimeOffset? LastCommit,
	bool StatusKnown,
	bool Active = true,
	DateTimeOffset? LastActivity = null)
{
	/// <summary>An index.lock older than this is left behind by a git that crashed or hangs, and blocks every other git.</summary>
	public static readonly TimeSpan StaleLock = TimeSpan.FromSeconds(60);

	public bool HasStaleLock => IndexLockAge >= StaleLock;

	/// <summary>Worth a row in the panel: active, with changes, blocked or in the middle of a merge or rebase.</summary>
	public bool IsShown => Active || ChangedFiles > 0 || HasStaleLock || State is not null;
}

/// <summary>Every followed repository at one sample.</summary>
public sealed record GitActivity(IReadOnlyList<GitRepositoryStatus> Repositories)
{
	/// <summary>Lines added and removed and not committed yet, over the repositories git was asked about.</summary>
	public double ChangedLines
	{
		get
		{
			var known = Repositories.Where(repository => repository.StatusKnown).ToList();
			// Nothing asked yet (no repository active) reads as nothing changed, so the trace stays at zero.
			return known.Sum(repository => (double) repository.Insertions + repository.Deletions);
		}
	}
}
