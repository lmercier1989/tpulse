using Pulse.Core.Formatting;

namespace Pulse.Core.Presentation;

/// <summary>
/// The short messages Pulse shows after a key or in the status line, worded once for the terminal and the window. A
/// message is a sentence-case phrase without a full stop; a hint to a key reads "Key verb": "End live", "Esc clear".
/// </summary>
public static class Messages
{
	/// <summary>"Markers on", "Notifications off".</summary>
	public static string Switch(string what, bool on) => $"{what} {(on ? "on" : "off")}";

	public static string History(double seconds) => $"History {Units.Duration(seconds)}";

	public static string Sampling(double seconds) => $"Sampling every {Units.Duration(seconds)}";

	public static string FrameRate(int fps) => $"Up to {fps} fps";

	public const string Paused = "Paused · Space resume";

	public const string Resumed = "Live";

	/// <summary>"◀ 45 s back · End live": the view is held at an earlier moment.</summary>
	public static string Back(double seconds) => $"◀ {Units.Duration(seconds)} back · End live";

	/// <summary>"Frozen 14:32:05 · Esc let go".</summary>
	public static string Frozen(string time) => $"Frozen {time} · Esc let go";

	/// <summary>"Selection 1 min 5 s · Enter zoom · E save · Y copy · B baseline · Esc clear".</summary>
	public static string Selection(double seconds) => $"Selection {Units.Duration(seconds)} · Enter zoom · E save · Y copy · B baseline · Esc clear";

	public static string Zoomed(double seconds) => $"Zoomed to {Units.Duration(seconds)} · Backspace zoom out";

	/// <summary>"Baseline 1 min 5 s · B clear": shown while no selection is compared with it.</summary>
	public static string Baseline(double seconds) => $"Baseline {Units.Duration(seconds)} · B clear";

	public static string BaselineSet(double seconds) => $"Baseline set ({Units.Duration(seconds)}) · select another range to compare";

	public const string BaselineCleared = "Baseline cleared";

	public const string BaselineNeedsSelection = "Drag across a graph to select a range, then B";

	public const string SelectionEmpty = "The selection holds no samples";

	public static string Saved(int samples, string path) => $"Saved {samples} samples to {path}";

	public static string SaveFailed(string reason) => $"Not saved: {reason}";

	public const string Copied = "Copied to the clipboard";

	public const string NoTerminalProgram = "No terminal program found · set $TERMINAL";

	public const string ClipboardBusy = "The clipboard is busy · Y again";

	public static string Stacked(bool on) => on ? "CPU stacked by process" : "CPU as one trace";

	public static string Details(bool inPanel) => inPanel ? "Details in the side panel" : "Details next to the mouse";

	public static string Topmost(bool on) => on ? "Always on top" : "Not on top";

	/// <summary>"Following devenv as graph 8".</summary>
	public static string Following(string name, char key) => $"Following {name} as graph {key}";

	public const string CannotFollow = "Up to three extra graphs, and only on this machine";

	public static string Marked(string time) => $"Marked {time}";

	public const string SectionHidden = "Section hidden · L shows it again";

	/// <summary>"Ended dotnet (process 12345)".</summary>
	public static string Ended(string name, int processId) => $"Ended {name} (process {processId})";

	/// <summary>"dotnet was not ended: Access is denied".</summary>
	public static string NotEnded(string name, string reason) => $"{name} was not ended: {reason}";

	/// <summary>"dotnet has ended already".</summary>
	public static string ProcessGone(string name) => $"{name} has ended already";

	public const string LetGo = "Moment let go";

	public const string SelectionCleared = "Selection cleared";

	public const string GraphsBack = "The graphs from before";

	public const string NothingToGoBack = "Live · nothing to go back from";
}
