using Pulse.Core.Metrics;

namespace Pulse.Core.Presentation;

/// <summary>Where a key or a mouse gesture works.</summary>
[Flags]
public enum Surface
{
	Terminal = 1,
	Window = 2,
	Both = Terminal | Window,
}

/// <summary>One row of the overview: the key or gesture, what it does, and for a marker its colour.</summary>
/// <param name="Keys">"Space", "← →", "1-7"; for a marker its name.</param>
/// <param name="Action">What it does, as a short sentence without a full stop.</param>
/// <param name="Surface">Where it works.</param>
/// <param name="Marker">The kind of event a legend row stands for; its colour goes in front of the name.</param>
public sealed record KeyBinding(string Keys, string Action, Surface Surface = Surface.Both, EventKind? Marker = null);

/// <summary>A group of rows under a title: "TIME", "MOUSE", "MARKERS".</summary>
public sealed record KeyGroup(string Title, IReadOnlyList<KeyBinding> Rows);

/// <summary>
/// Every key, mouse gesture and marker colour of Pulse in one place, grouped the same way for the terminal (<c>?</c>),
/// the window (<c>F1</c>) and the README, and every command a shell can run (the second page of <c>?</c>). A key does the
/// same on both surfaces, or is listed for one only.
/// </summary>
public static class KeyMap
{
	/// <summary>The groups for <paramref name="surface"/>, with the extra graphs named after the built-in ones.</summary>
	/// <param name="surface">The terminal or the window.</param>
	/// <param name="extraGraphs">The titles of the <c>-process</c> and <c>-counter</c> graphs, in key order.</param>
	/// <param name="replay">Whether a recording plays: Space plays it, H zooms it, and nothing samples.</param>
	public static IReadOnlyList<KeyGroup> For(Surface surface, IReadOnlyList<string> extraGraphs, bool replay = false)
	{
		var builtin = Processing.MetricKinds.Builtin.Count;
		var graphs = new List<KeyBinding>
		{
			new(Processing.Graph.KeyRange(1, builtin), "Show or hide graph 1 (CPU) to 7 (Git)"),
		};
		if (extraGraphs.Count > 0)
		{
			graphs.Add(new(Processing.Graph.KeyRange(builtin + 1, extraGraphs.Count), $"Show or hide {string.Join(", ", extraGraphs)}"));
		}

		graphs.AddRange(
		[
			new($"Shift + {Processing.Graph.KeyRange(1, builtin + extraGraphs.Count)}", "Size of that graph: normal, tall or folded"),
			new("K", "A trace per CPU core"),
			new("S", "CPU stacked by process, or as one trace", Surface.Terminal),
			new("P", "Lowest and highest value in the headers"),
			new("M", "Markers on or off"),
		]);

		KeyGroup[] all =
		[
			new("Time",
			[
				new("Space", replay ? "Play or stop the recording" : "Pause or resume"),
				new("← →", "Scroll back and forth (Shift: a whole window)"),
				new("Home, End", "Oldest moment, back to live"),
				new("H", replay ? "Zoom: 30 s up to the whole recording" : "History: 30 s, 1 min, 5 min"),
				new("R", "Sampling: every 0.5, 1 or 2 s"),
			]),
			new("Mouse",
			[
				new("Point", "That moment: values and busiest processes"),
				new("Hold", "Freeze that moment while you read it"),
				new("Click", "Keep that moment until the next click or Esc", Surface.Terminal),
				new("Drag", "Select a range of time", Surface.Terminal),
				new("Wheel", "Scroll through time"),
				new("Right-click", "Menu for what is under the mouse", Surface.Terminal),
				new("Right-drag", "Pan through time", Surface.Terminal),
				new("Click a title", "Fold or unfold that graph"),
				new("Double-click", "Make that graph tall", Surface.Window),
				new("Right-click", "Menu for that graph and moment", Surface.Window),
			]),
			new("Selection",
			[
				new("Enter", "Zoom in on the selection", Surface.Terminal),
				new("Backspace", "Zoom out again", Surface.Terminal),
				new("E", "Save the selection as a recording", Surface.Terminal),
				new("Y", "Copy its summary to the clipboard", Surface.Terminal),
				new("B", "Make it the baseline (no selection: clear)", Surface.Terminal),
				new("Esc", "Let go of a moment, clear the selection", Surface.Terminal),
			]),
			new("Graphs", graphs),
			new("Markers",
			[
				new("git", "Commit, checkout, merge, rebase, reset, pull", Marker: EventKind.Git),
				new("mark", "tpulse -mark; start and end of tpulse --", Marker: EventKind.Mark),
				new("spike", "Far above its usual level, cause named", Marker: EventKind.Spike),
				new("leak", "Memory that kept growing for ten minutes", Marker: EventKind.Leak),
				new("error", OperatingSystem.IsWindows() ? "An error or critical event in the Windows logs" : "An error or critical message in the systemd journal", Marker: EventKind.Error),
			]),
			new("Screen",
			[
				new("I", "Side panel on or off", Surface.Terminal),
				new("L", "Layout: order and show graphs and sections", Surface.Terminal),
				new("Menu key", "The menu at the mouse (also Shift + F10)", Surface.Terminal),
				new("D", "Moment details: at the mouse or in the panel", Surface.Terminal),
				new("C", "Compact layout"),
				new("T", "Always on top", Surface.Window),
				new("F", "Frame rate: 30 or 60 fps"),
				new("N", OperatingSystem.IsWindows() ? "Windows notifications" : "Desktop notifications"),
				new("?", "This overview; again: the shell commands", Surface.Terminal),
				new("F1, ?", "This overview", Surface.Window),
				new("Q, Ctrl + C", "Quit", Surface.Terminal),
				new("Ctrl + Q", "Close", Surface.Window),
			]),
		];

		return [.. all
			.Select(group => new KeyGroup(group.Title, [.. group.Rows.Where(row => row.Surface.HasFlag(surface) && (!replay || row.Keys is not ("R" or "N")))]))
			.Where(group => group.Rows.Count > 0)];
	}

	/// <summary>The footer of the terminal: the keys to start with, the rest is behind <c>?</c>.</summary>
	public static string Hints(int graphCount, bool replay) => replay
		? $"Space play · ← → scroll · H zoom · 1-{Processing.Graph.KeyOf(graphCount - 1)} graphs · ? help · Q quit"
		: $"Space pause · ← → scroll · Drag select · Right-click menu · 1-{Processing.Graph.KeyOf(graphCount - 1)} graphs · ? help · Q quit";

	/// <summary>What a shell can run: the second page of <c>?</c>, so no option has to be remembered.</summary>
	public static IReadOnlyList<KeyBinding> ShellCommands { get; } = OperatingSystem.IsWindows() ? WindowsShellCommands() : LinuxShellCommands();

	private static KeyBinding[] WindowsShellCommands() =>
	[
		new("tpulse", "Open Pulse in a tab of its own"),
		new("tpulse -- dotnet build", "Measure a command: marks at its start and end, what it cost after"),
		new("tpulse -mark deploy started", "Put a mark on every running Pulse"),
		new("tpulse -cpu -memory -disk", "Only these graphs (also -gpu -network -claude -git -all)"),
		new("tpulse -process devenv", "An extra graph for a process, by name or process id"),
		new("tpulse -counter \"\\Memory\\Cache Bytes\"", "An extra graph for any performance counter"),
		new("tpulse -host server01", "Watch another Windows machine"),
		new("tpulse -git D:\\Repos", "Follow the repositories in that folder only"),
		new("tpulse -history 5m -interval 0.5s", "How much history, how often a sample"),
		new("tpulse -record", "Save every sample to a CSV file in Documents\\Pulse"),
		new("tpulse -replay run.csv", "Play a recording (or a saved selection) back"),
		new("tpulse -notify", "Windows notifications: high load, full disk, git blocked, growing memory"),
		new("tpulse -profile dev", "Start with the options saved as a profile"),
		new("tpulse -save-profile dev -cpu -git", "Save options as a profile"),
		new("tpulse -profiles", "List the profiles"),
		new("tpulse -window", "A separate window instead of the terminal"),
		new("tpulse -here", "Draw in this tab instead of a new one"),
		new("tpulse -braille", "Braille characters instead of Sixel graphics"),
		new("tpulse -help", "Every option, with examples"),
	];

	/// <summary>The commands of the Linux build: no other machines, counters, window or Windows Terminal tab.</summary>
	private static KeyBinding[] LinuxShellCommands() =>
	[
		new("tpulse -- make -j8", "Measure a command: marks at its start and end, what it cost after"),
		new("tpulse -mark deploy started", "Put a mark on every running Pulse"),
		new("tpulse -cpu -memory -disk", "Only these graphs (also -gpu -network -claude -git -all)"),
		new("tpulse -process firefox", "An extra graph for a process, by name or process id"),
		new("tpulse -git ~/src", "Follow the repositories in that folder only"),
		new("tpulse -history 5m -interval 0.5s", "How much history, how often a sample"),
		new("tpulse -record", "Save every sample to a CSV file in ~/Documents/Pulse"),
		new("tpulse -replay run.csv", "Play a recording (or a saved selection) back"),
		new("tpulse -notify", "Desktop notifications: high load, full disk, git blocked, growing memory"),
		new("tpulse -profile dev", "Start with the options saved as a profile"),
		new("tpulse -save-profile dev -cpu -git", "Save options as a profile"),
		new("tpulse -profiles", "List the profiles"),
		new("tpulse -braille", "Braille characters instead of Sixel graphics"),
		new("tpulse -help", "Every option, with examples"),
	];
}
