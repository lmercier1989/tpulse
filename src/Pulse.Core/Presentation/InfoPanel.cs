using System.Globalization;

using Pulse.Core.Formatting;
using Pulse.Core.Metrics;

namespace Pulse.Core.Presentation;

/// <summary>What a row of the info panel shows.</summary>
public enum PanelLineKind
{
	Blank,
	Section,
	Field,
	Gauge,
	Note,
}

/// <summary>
/// One row of the info panel. <paramref name="Priority"/> decides what goes first when the terminal is not tall
/// enough: higher numbers are dropped first, section titles (0) last.
/// </summary>
/// <param name="Kind">Section title, label and value, a gauge, an indented note, or an empty row.</param>
/// <param name="Label">Section title, field label or gauge name.</param>
/// <param name="Text">Field value or note text, left in the value column; for a section, its detail (a time, a length),
/// shown after the title as it is written.</param>
/// <param name="Priority">0 is kept longest.</param>
/// <param name="Percent">Gauge fill, 0 to 100.</param>
/// <param name="Emphasis">Shown in the bright text colour.</param>
/// <param name="Warning">Shown in the warning colour: a Windows error, a git operation in progress, a stuck lock.</param>
/// <param name="Right">A short value right-aligned at the end of the row: a load, a size, a time ago.</param>
/// <param name="Swatch">The band of the stacked CPU graph whose colour goes in front of the label; -1 for none.</param>
/// <param name="Event">The kind of event a row stands for; its marker colour goes in front of the label.</param>
public readonly record struct PanelLine(
	PanelLineKind Kind,
	string Label,
	string Text,
	int Priority,
	double Percent = double.NaN,
	bool Emphasis = false,
	bool Warning = false,
	string Right = "",
	int Swatch = -1,
	EventKind? Event = null)
{
	/// <summary>The columns a swatch or an event mark takes in front of the label.</summary>
	public int MarkWidth => Swatch >= 0 || Event is not null ? 2 : 0;
}

/// <summary>
/// A moment looked into with the mouse: the panel then starts with what was busiest at that moment and what happened
/// around it, and which processes carried the load over the whole visible window.
/// </summary>
/// <param name="At">The wall-clock time of the moment.</param>
/// <param name="Pinned">Frozen with the mouse button, rather than under the mouse.</param>
/// <param name="Events">The events within a few seconds of the moment.</param>
/// <param name="InView">The process names with the highest average CPU over the visible window.</param>
public sealed record PanelFocus(DateTimeOffset At, bool Pinned, IReadOnlyList<PulseEvent> Events, IReadOnlyList<ProcessLoad> InView);

/// <summary>
/// The side panel next to the graphs: the machine (host, Windows version, uptime, processes, battery), Claude, git, the
/// busiest processes, the latest events, the network connection and the space on every volume, of this machine or of
/// the one read with <c>-host</c>. Values that are compared (loads, sizes, times) are right-aligned. It never needs
/// more rows than it gets: <see cref="Fit"/> leaves out the least important rows first, and <see cref="FitColumns"/>
/// spreads the sections over two columns when the panel is wide.
/// </summary>
public static class InfoPanel
{
	/// <summary>Columns of the label column of a field, unless <see cref="LabelWidthFor"/> finds a better one.</summary>
	public const int LabelWidth = 10;

	/// <summary>The events the panel lists at most.</summary>
	public const int MaximumEvents = 4;

	/// <summary>The repositories the panel lists at most; the rest is counted.</summary>
	public const int MaximumRepositories = 6;

	/// <summary>The busiest processes the panel lists live; looking into a moment shows more.</summary>
	public const int MaximumTop = 3;

	/// <summary>The busiest processes listed for a moment looked into.</summary>
	public const int MaximumFocusTop = 6;

	private const int BlankPriority = 9;
	private const int MinimumLabelWidth = 8;
	private const int MaximumLabelWidth = 16;

	/// <param name="details">The slow facts of the machine; null until they are first read.</param>
	/// <param name="sample">The latest sample; null before the first one.</param>
	/// <param name="uptime">How long the machine has been up; null when it is not known (yet).</param>
	/// <param name="events">The latest events, newest first; the panel lists the first few.</param>
	/// <param name="focus">A moment looked into with the mouse; <paramref name="sample"/> is then the sample of that moment.</param>
	public static List<PanelLine> Build(SystemDetails? details, MetricSnapshot? sample, TimeSpan? uptime, IReadOnlyList<PulseEvent>? events = null, PanelFocus? focus = null)
	{
		var culture = CultureInfo.CurrentCulture;
		var now = sample?.RecordedAt ?? TimeProvider.System.GetUtcNow();
		var lines = new List<PanelLine>();
		if (focus is not null && sample is not null)
		{
			AddFocus(lines, focus, sample);
			lines.Add(Blank());
		}

		lines.Add(Section("System"));

		if (details is not null)
		{
			lines.Add(Field("host", details.HostName, 1, emphasis: true, right: details.Remote ? "remote" : string.Empty));
			AddIf(lines, "windows", details.OperatingSystem, 3);
		}

		if (uptime is { } up)
		{
			lines.Add(Field("uptime", FormatUptime(up), 1));
		}

		if (sample is not null && double.IsFinite(sample.ProcessCount))
		{
			var threads = double.IsFinite(sample.ThreadCount) ? $"{sample.ThreadCount.ToString("N0", culture)} threads" : string.Empty;
			lines.Add(Field("processes", sample.ProcessCount.ToString("N0", culture), 2, right: threads));
		}

		if (details?.Battery is { } battery)
		{
			var state = battery.Charging ? "charging" : battery.OnPower ? "on power" : string.Empty;
			lines.Add(Field("battery", battery.Percent.ToString("0", culture) + "%", 1, right: state));
		}

		if (sample is not null && (sample.ClaudeProcesses.IsRunning || sample.ClaudeSessions.Count > 0))
		{
			AddClaude(lines, sample);
		}

		if (sample?.Git is { Repositories.Count: > 0 } git)
		{
			AddGit(lines, git, now);
		}

		// While looking into a moment, its busiest processes and events are listed at the top already.
		if (sample is not null && focus is null)
		{
			AddTop(lines, "Top CPU", [.. sample.TopCpu.Take(MaximumTop)], Cpu, 4);
			AddTop(lines, "Top I/O", [.. sample.TopIo.Take(MaximumTop)], Io, 5);
		}

		if (sample is { Leaks.Count: > 0 })
		{
			AddLeaks(lines, sample.Leaks);
		}

		if (events is { Count: > 0 } && focus is null)
		{
			AddEvents(lines, events, now);
		}

		if (details?.Network is { } network)
		{
			lines.Add(Blank());
			lines.Add(Section("Network"));
			lines.Add(network.IPv4 is null ? Field("ipv4", "no address", 1) : Field("ipv4", network.IPv4, 1, emphasis: true));
			AddIf(lines, "gateway", network.Gateway, 2);
			// The ping goes to the gateway here, and to the machine itself when it is read remotely.
			if ((network.Gateway is not null || details.Remote) && sample is not null)
			{
				lines.Add(Field("ping", double.IsFinite(sample.PingMs) ? FormatLatency(sample.PingMs) : "no reply", 3));
			}

			AddIf(lines, "dns", network.Dns, 3);
			AddIf(lines, "ipv6", network.IPv6, 5);
			lines.Add(Field("adapter", network.Adapter, 4));
			if (network.LinkBitsPerSecond > 0)
			{
				lines.Add(Field("link", Units.BitsPerSecond(network.LinkBitsPerSecond), 5));
			}
		}

		if (details is { Disks.Count: > 0 })
		{
			lines.Add(Blank());
			lines.Add(Section("Disks"));
			foreach (var disk in details.Disks)
			{
				lines.Add(new PanelLine(PanelLineKind.Gauge, disk.Name, string.Empty, 1, disk.UsedPercent));
				lines.Add(Note(disk.Label?.Trim() ?? string.Empty, 4, right: $"{Units.Bytes(disk.FreeBytes)} free of {Units.Bytes(disk.TotalBytes)}"));
			}
		}

		return lines;
	}

	/// <summary>The rows above the panel of a recording played back: its file, when and how long it ran, and the moment shown.</summary>
	/// <param name="file">The file name of the recording.</param>
	/// <param name="start">The time of its first row.</param>
	/// <param name="end">The time of its last row.</param>
	/// <param name="at">The recorded time at the right edge of the graphs.</param>
	public static List<PanelLine> BuildReplay(string file, DateTimeOffset start, DateTimeOffset end, DateTimeOffset? at)
	{
		var culture = CultureInfo.CurrentCulture;
		var lines = new List<PanelLine>
		{
			Section("Replay"),
			Field("file", file, 1, emphasis: true),
			Field("recorded", start.LocalDateTime.ToString("g", culture), 2, right: FormatLength(end - start)),
		};
		if (at is { } moment)
		{
			lines.Add(Field("shown", moment.LocalDateTime.ToString("T", culture), 1, emphasis: true));
		}

		lines.Add(Blank());
		return lines;
	}

	/// <summary>
	/// Cuts <paramref name="lines"/> down to <paramref name="rows"/>: blank separators go first, then the
	/// highest priority from the bottom up. A section title without rows under it goes too.
	/// </summary>
	public static List<PanelLine> Fit(IReadOnlyList<PanelLine> lines, int rows)
	{
		var result = new List<PanelLine>(lines);
		if (rows <= 0)
		{
			result.Clear();
			return result;
		}

		Tidy(result);
		while (result.Count > rows)
		{
			var highest = result.Max(line => line.Priority);
			var index = result.FindLastIndex(line => line.Priority == highest);
			if (highest == 0)
			{
				index = result.Count - 1;
			}

			result.RemoveAt(index);
			Tidy(result);
		}

		return result;
	}

	/// <summary>
	/// The rows of <paramref name="columns"/> columns of <paramref name="rows"/> rows each: as much as fits in all of them,
	/// broken between two sections, so a section never starts at the bottom of one column and goes on in the next. One
	/// column is <see cref="Fit"/>.
	/// </summary>
	public static List<List<PanelLine>> FitColumns(IReadOnlyList<PanelLine> lines, int rows, int columns)
	{
		if (columns <= 1 || rows <= 0)
		{
			return [Fit(lines, rows)];
		}

		for (var budget = rows * 2; budget > 0; budget--)
		{
			var fitted = Fit(lines, budget);

			// The break at a section title that evens out the two columns best, with neither taller than the rows.
			var best = -1;
			for (var split = 1; split < fitted.Count; split++)
			{
				if (fitted[split].Kind == PanelLineKind.Section && split <= rows && fitted.Count - split <= rows
					&& (best < 0 || Math.Abs(fitted.Count - (2 * split)) < Math.Abs(fitted.Count - (2 * best))))
				{
					best = split;
				}
			}

			if (best > 0)
			{
				var first = fitted.GetRange(0, best);
				while (first.Count > 0 && first[^1].Kind == PanelLineKind.Blank)
				{
					first.RemoveAt(first.Count - 1);
				}

				return [first, fitted.GetRange(best, fitted.Count - best)];
			}

			if (fitted.Count <= rows)
			{
				return [fitted, []]; // One section only: it stays in one column.
			}
		}

		return [Fit(lines, rows), []];
	}

	/// <summary>
	/// The label column for <paramref name="lines"/> in a panel of <paramref name="width"/> columns: wide enough for the
	/// longest label, from 8 to 16 columns and at most about half the panel, so values keep their room.
	/// </summary>
	public static int LabelWidthFor(IEnumerable<PanelLine> lines, int width)
	{
		var longest = lines.Where(line => line.Kind == PanelLineKind.Field).Select(line => line.Label.Length + line.MarkWidth).DefaultIfEmpty(0).Max();
		return Math.Clamp(longest + 2, MinimumLabelWidth, Math.Max(MinimumLabelWidth, Math.Min(MaximumLabelWidth, width * 45 / 100)));
	}

	/// <summary>
	/// The label column of every row: each section gets its own, fitted to its own labels (<see cref="LabelWidthFor"/>),
	/// so one long repository name does not push the values of every other section aside.
	/// </summary>
	public static int[] LabelWidths(IReadOnlyList<PanelLine> rows, int width)
	{
		var widths = new int[rows.Count];
		var start = 0;
		for (var i = 0; i <= rows.Count; i++)
		{
			if (i == rows.Count || (i > start && rows[i].Kind == PanelLineKind.Section))
			{
				var section = LabelWidthFor(rows.Skip(start).Take(i - start), width);
				Array.Fill(widths, section, start, i - start);
				start = i;
			}
		}

		return widths;
	}

	/// <summary>The length of a recording: "46 s", "12 min", "1 h 5 min".</summary>
	internal static string FormatLength(TimeSpan length)
		=> length.TotalMinutes < 1 ? $"{Math.Max(0, (int) length.TotalSeconds)} s" : FormatUptime(length);

	/// <summary>"12 min", "4 h 12 min" or "3 d 4 h".</summary>
	public static string FormatUptime(TimeSpan uptime)
	{
		if (uptime < TimeSpan.Zero)
		{
			uptime = TimeSpan.Zero;
		}

		return uptime.TotalDays >= 1 ? $"{(int) uptime.TotalDays} d {uptime.Hours} h"
			: uptime.TotalHours >= 1 ? $"{(int) uptime.TotalHours} h {uptime.Minutes} min"
			: $"{(int) uptime.TotalMinutes} min";
	}

	/// <summary>"now", "45 s", "3 min".</summary>
	internal static string FormatAgo(TimeSpan ago) => ago.TotalSeconds < 5 ? "now"
		: ago.TotalMinutes < 1 ? $"{(int) ago.TotalSeconds} s"
		: ago.TotalHours < 1 ? $"{(int) ago.TotalMinutes} min"
		: ago.TotalDays < 2 ? $"{(int) ago.TotalHours} h"
		: $"{(int) ago.TotalDays} d";

	/// <summary>"<1 ms" for a local gateway, else whole milliseconds.</summary>
	internal static string FormatLatency(double milliseconds)
		=> milliseconds < 1 ? "<1 ms" : milliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms";

	/// <summary>
	/// What Claude costs the machine, split into the app, its sessions and the tools those start (load right-aligned),
	/// and the sessions that answered in the last 10 minutes with their project, context size and last activity.
	/// </summary>
	private static void AddClaude(List<PanelLine> lines, MetricSnapshot sample)
	{
		var usage = sample.ClaudeProcesses;
		lines.Add(Blank());
		lines.Add(Section("Claude"));
		if (usage.IsRunning)
		{
			lines.Add(Field("app", string.Empty, 2, right: Load(usage.AppCpu, usage.AppBytes)));
			lines.Add(Field("sessions", $"{usage.Sessions}", 1, emphasis: true, right: Load(usage.SessionsCpu, usage.SessionsBytes)));
			lines.Add(Field("tools", $"{usage.Tools}", 1, right: Load(usage.ToolsCpu, usage.ToolsBytes)));
		}

		foreach (var session in sample.ClaudeSessions)
		{
			var project = string.IsNullOrEmpty(session.Project) ? "session" : session.Project;
			var ago = (sample.RecordedAt ?? TimeProvider.System.GetUtcNow()) - session.LastActivity;
			lines.Add(Note($"{project} · ctx {Units.Tokens(session.ContextTokens)}", 4, right: FormatAgo(ago)));
		}
	}

	/// <summary>
	/// The repositories worth a look, latest activity first: active ones, ones with changes, ones blocked or in the middle
	/// of a merge or rebase. One row each (name, branch, changes not committed yet); the first two get a second row
	/// with their files, upstream and last commit when there is room. An index.lock that blocks git is a warning. The
	/// ones that rest are only counted.
	/// </summary>
	private static void AddGit(List<PanelLine> lines, GitActivity git, DateTimeOffset now)
	{
		lines.Add(Blank());
		lines.Add(Section("Git"));
		var shown = git.Repositories.Where(repository => repository.IsShown).OrderByDescending(repository => repository.LastActivity).ToList();
		for (var index = 0; index < shown.Count && index < MaximumRepositories; index++)
		{
			var repository = shown[index];
			var branch = repository.State is null ? repository.Branch ?? "?" : $"{repository.Branch} · {repository.State}";
			var changes = !repository.StatusKnown ? string.Empty
				: repository.ChangedFiles == 0 ? "clean"
				: $"+{Units.Number(repository.Insertions)} −{Units.Number(repository.Deletions)}";
			lines.Add(Field(repository.Name, branch, index < 3 ? 1 : 5, emphasis: true, warning: repository.State is not null, right: changes));
			if (index < 2)
			{
				var files = repository.StatusKnown && repository.ChangedFiles > 0 ? $"{repository.ChangedFiles} files" : null;
				var upstream = repository.Ahead is { } ahead ? $"↑{ahead} ↓{repository.Behind ?? 0}" : null;
				var detail = string.Join(" · ", new[] { files, upstream }.Where(part => part is not null));
				var commit = repository.LastCommit is { } last ? $"commit {FormatAgo(now - last)}" : string.Empty;
				if (detail.Length > 0 || commit.Length > 0)
				{
					lines.Add(Note(detail, 6, right: commit));
				}
			}

			if (repository.HasStaleLock)
			{
				lines.Add(Note("index.lock blocks git", 1, warning: true, right: FormatAgo(repository.IndexLockAge!.Value)));
			}
		}

		var listed = Math.Min(shown.Count, MaximumRepositories);
		if (listed < git.Repositories.Count)
		{
			lines.Add(Note(listed == 0 ? $"{git.Repositories.Count} followed, none active" : $"{git.Repositories.Count - listed} more followed", listed == 0 ? 2 : 5));
		}
	}

	/// <summary>The busiest process names, the load right-aligned; "×24" when that many processes share the name.</summary>
	/// <summary>Only the lines of a moment looked into, for the flyout next to the mouse.</summary>
	public static List<PanelLine> BuildFocus(MetricSnapshot sample, PanelFocus focus)
	{
		var lines = new List<PanelLine>();
		AddFocus(lines, focus, sample);
		return lines;
	}

	/// <summary>The processes whose memory kept growing for ten minutes: how much, and how big they are now.</summary>
	private static void AddLeaks(List<PanelLine> lines, IReadOnlyList<LeakSuspect> leaks)
	{
		var culture = CultureInfo.CurrentCulture;
		lines.Add(Blank());
		lines.Add(Section("Growing memory"));
		foreach (var leak in leaks.Take(3))
		{
			lines.Add(Field(leak.Name, $"+{Units.Bytes(leak.GrowthBytes, culture)} in {leak.Minutes.ToString("0", culture)} min", 2, warning: true, right: Units.Bytes(leak.Bytes, culture)));
		}
	}

	private static void AddTop(List<PanelLine> lines, string title, IReadOnlyList<ProcessLoad> loads, Func<ProcessLoad, string> format, int priority)
	{
		if (loads.Count == 0)
		{
			return;
		}

		lines.Add(Blank());
		lines.Add(Section(title));
		foreach (var load in loads)
		{
			lines.Add(Field(load.Name, load.Count > 1 ? $"×{load.Count}" : string.Empty, priority, right: format(load)));
		}
	}

	/// <summary>The latest events, newest first, how long ago right-aligned; Windows errors in the warning colour.</summary>
	private static void AddEvents(List<PanelLine> lines, IReadOnlyList<PulseEvent> events, DateTimeOffset now)
	{
		lines.Add(Blank());
		lines.Add(Section("Events"));
		for (var i = 0; i < events.Count && i < MaximumEvents; i++)
		{
			var item = events[i];
			var text = string.IsNullOrEmpty(item.Text) ? item.Source : $"{item.Source} · {item.Text}";
			lines.Add(Field(item.Action, text, i < 2 ? 3 : 5, warning: item.Kind == EventKind.Error, right: FormatAgo(now - item.At)) with { Event = item.Kind });
		}
	}

	/// <summary>
	/// The moment under the mouse (or frozen with the mouse button): what was busiest then, CPU and I/O, the events around it, and
	/// which processes carried the load over the whole visible window. It comes first and is the last to be cut.
	/// </summary>
	private static void AddFocus(List<PanelLine> lines, PanelFocus focus, MetricSnapshot sample)
	{
		var culture = CultureInfo.CurrentCulture;
		lines.Add(Section("Moment", $"{focus.At.LocalDateTime.ToString("T", culture)}{(focus.Pinned ? " · frozen" : string.Empty)}"));
		var cpu = sample.TopCpu.Take(MaximumFocusTop).ToList();
		for (var i = 0; i < cpu.Count; i++)
		{
			lines.Add(Field(cpu[i].Name, cpu[i].Count > 1 ? $"×{cpu[i].Count}" : string.Empty, i < 3 ? 1 : 3, emphasis: i == 0, right: Cpu(cpu[i])));
		}

		foreach (var load in sample.TopIo.Take(MaximumTop))
		{
			lines.Add(Field(load.Name, load.Count > 1 ? $"×{load.Count}" : string.Empty, 3, right: Io(load)));
		}

		if (cpu.Count == 0 && sample.TopIo.Count == 0)
		{
			lines.Add(Note("no process list for this moment", 2));
		}

		foreach (var item in focus.Events.Take(MaximumEvents))
		{
			var text = string.IsNullOrEmpty(item.Text) ? item.Source : $"{item.Source} · {item.Text}";
			lines.Add(Field(item.Action, text, 2, warning: item.Kind == EventKind.Error, right: item.At.LocalDateTime.ToString("T", culture)) with { Event = item.Kind });
		}

		if (focus.InView.Count > 0)
		{
			lines.Add(Blank());
			lines.Add(Section("In view", "average CPU"));
			foreach (var load in focus.InView)
			{
				lines.Add(Field(load.Name, load.Count > 1 ? $"×{load.Count}" : string.Empty, 3, right: Cpu(load)));
			}
		}
	}

	/// <summary>The CPU of a process, as everywhere: one decimal.</summary>
	internal static string Cpu(ProcessLoad load) => Units.Share(load.Value);

	/// <summary>The I/O of a process, in bytes per second.</summary>
	internal static string Io(ProcessLoad load) => Units.BytesPerSecond(load.Value);

	/// <summary>"×7" for a name that stands for more than one process, else nothing.</summary>
	internal static string CountOf(ProcessLoad load) => load.Count > 1 ? $"×{load.Count}" : string.Empty;

	private static string Load(double cpu, double bytes)
		=> $"{(double.IsFinite(cpu) ? cpu.ToString("0.0", CultureInfo.CurrentCulture) + "%" : Units.Unavailable)} · {Units.Bytes(bytes)}";

	/// <summary>A section title in capitals, with an optional detail written as it is: "SELECTION 14:32:05 · 1 min 5 s".</summary>
	internal static PanelLine Section(string title, string detail = "") => new(PanelLineKind.Section, title.ToUpperInvariant(), detail, 0);

	private static PanelLine Field(string label, string text, int priority, bool emphasis = false, bool warning = false, string right = "")
		=> new(PanelLineKind.Field, label, text, priority, Emphasis: emphasis, Warning: warning, Right: right);

	private static PanelLine Note(string text, int priority, bool warning = false, string right = "")
		=> new(PanelLineKind.Note, string.Empty, text, priority, Warning: warning, Right: right);

	private static PanelLine Blank() => new(PanelLineKind.Blank, string.Empty, string.Empty, BlankPriority);

	private static void AddIf(List<PanelLine> lines, string label, string? text, int priority)
	{
		if (!string.IsNullOrWhiteSpace(text))
		{
			lines.Add(Field(label, text, priority));
		}
	}

	/// <summary>No section title without content, no blank row at the start, at the end or twice in a row.</summary>
	private static void Tidy(List<PanelLine> lines)
	{
		for (var i = lines.Count - 1; i >= 0; i--)
		{
			var next = i + 1 < lines.Count ? lines[i + 1].Kind : PanelLineKind.Blank;
			var empty = lines[i].Kind == PanelLineKind.Section && next is PanelLineKind.Blank or PanelLineKind.Section;
			var stray = lines[i].Kind == PanelLineKind.Blank && (i == 0 || i == lines.Count - 1 || next == PanelLineKind.Blank);
			if (empty || stray)
			{
				lines.RemoveAt(i);
			}
		}
	}
}
