using System.Text.Json.Serialization;

using Pulse.Core.Processing;

namespace Pulse.Core.Settings;

/// <summary>How much room a graph takes.</summary>
public enum ChannelSize
{
	Normal,
	Tall,
	Collapsed,
}

/// <summary>Per-graph preferences.</summary>
public sealed class ChannelSettings
{
	public bool Visible { get; set; } = true;

	public ChannelSize Size { get; set; } = ChannelSize.Normal;
}

/// <summary>Window position and size in device-independent pixels.</summary>
public sealed class WindowBounds
{
	public double Left { get; set; }

	public double Top { get; set; }

	public double Width { get; set; }

	public double Height { get; set; }

	public bool Maximized { get; set; }
}

/// <summary>User preferences, stored as JSON in <c>%LOCALAPPDATA%\Pulse\settings.json</c>.</summary>
public sealed class PulseSettings
{
	public const double MinHistorySeconds = 10;
	public const double MaxHistorySeconds = 600;
	public const double MinIntervalSeconds = 0.25;
	public const double MaxIntervalSeconds = 5;
	public const int MinFps = 15;
	public const int MaxFps = 120;

	/// <summary>The history presets the H key cycles through.</summary>
	public static IReadOnlyList<double> HistoryPresets { get; } = [30, 60, 300];

	/// <summary>The sampling interval presets the R key cycles through.</summary>
	public static IReadOnlyList<double> IntervalPresets { get; } = [0.5, 1, 2];

	private static readonly int ChannelCount = MetricKinds.Builtin.Count;

	public double HistorySeconds { get; set; } = 60;

	public double IntervalSeconds { get; set; } = 1;

	public int TargetFps { get; set; } = 60;

	public bool ShowCores { get; set; }

	public bool ShowMinMax { get; set; } = true;

	public bool Compact { get; set; }

	public bool AlwaysOnTop { get; set; }

	/// <summary>The info panel beside the graphs in the terminal (disks, network, system).</summary>
	public bool ShowPanel { get; set; } = true;

	/// <summary>Markers on the time axis for git activity and Windows errors.</summary>
	public bool ShowMarkers { get; set; } = true;

	/// <summary>Windows notifications for alerts: a lasting load, a full disk, a git that is blocked.</summary>
	public bool Notify { get; set; }

	/// <summary>The CPU graph as stacked bands of the busiest processes rather than one trace (S in the terminal).</summary>
	public bool StackCpu { get; set; }

	/// <summary>Looking into a moment fills the side panel rather than a flyout at the mouse (T in the terminal).</summary>
	public bool FocusInPanel { get; set; }

	/// <summary>One entry per built-in graph (<see cref="MetricKinds.Builtin"/>), in enum order.</summary>
	public ChannelSettings[] Channels { get; set; } = CreateChannels();

	public WindowBounds? Window { get; set; }

	/// <summary>
	/// How far the trace runs behind the newest sample: two intervals, so every curve segment is final
	/// (a monotone cubic segment depends on the samples on both sides of it) before it scrolls into view.
	/// </summary>
	[JsonIgnore]
	public double DisplayDelaySeconds => (2 * IntervalSeconds) + 0.15;

	/// <summary>A longer silence than this (sleep, a stalled counter) breaks the trace.</summary>
	[JsonIgnore]
	public double GapSeconds => Math.Max(6, 3 * IntervalSeconds);

	public ChannelSettings Channel(MetricKind kind) => Channels[(int) kind];

	/// <summary>The preset after <paramref name="current"/>, wrapping around to the first.</summary>
	public static double NextPreset(IReadOnlyList<double> presets, double current)
	{
		foreach (var preset in presets)
		{
			if (preset > current + 1e-9)
			{
				return preset;
			}
		}

		return presets[0];
	}

	/// <summary>"30 s", "0.5 s", "5 min" or "1 min 5 s" in the given culture: <see cref="Formatting.Units.Duration"/>.</summary>
	public static string FormatDuration(double seconds, IFormatProvider? provider = null) => Formatting.Units.Duration(seconds, provider);

	/// <summary>Repairs values from an edited or older settings file.</summary>
	public void Normalize()
	{
		HistorySeconds = double.IsFinite(HistorySeconds) ? Math.Clamp(HistorySeconds, MinHistorySeconds, MaxHistorySeconds) : 60;
		IntervalSeconds = double.IsFinite(IntervalSeconds) ? Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds) : 1;
		TargetFps = Math.Clamp(TargetFps, MinFps, MaxFps);

		var channels = CreateChannels();
		for (var i = 0; i < channels.Length && Channels is not null && i < Channels.Length; i++)
		{
			if (Channels[i] is { } loaded && Enum.IsDefined(loaded.Size))
			{
				channels[i] = loaded;
			}
		}

		Channels = channels;

		// A height of 0 means "fit the graphs"; it is stored when the layout came from the command line.
		if (Window is { } window && !(double.IsFinite(window.Left) && double.IsFinite(window.Top)
									  && double.IsFinite(window.Width) && double.IsFinite(window.Height)
									  && window.Width > 0 && window.Height >= 0))
		{
			Window = null;
		}
	}

	public PulseSettings Clone() => new()
	{
		HistorySeconds = HistorySeconds,
		IntervalSeconds = IntervalSeconds,
		TargetFps = TargetFps,
		ShowCores = ShowCores,
		ShowMinMax = ShowMinMax,
		Compact = Compact,
		AlwaysOnTop = AlwaysOnTop,
		ShowPanel = ShowPanel,
		ShowMarkers = ShowMarkers,
		Notify = Notify,
		StackCpu = StackCpu,
		FocusInPanel = FocusInPanel,
		Channels = [.. Channels.Select(c => new ChannelSettings { Visible = c.Visible, Size = c.Size })],
		Window = Window is null ? null : new WindowBounds
		{
			Left = Window.Left,
			Top = Window.Top,
			Width = Window.Width,
			Height = Window.Height,
			Maximized = Window.Maximized,
		},
	};

	private static ChannelSettings[] CreateChannels()
	{
		var channels = new ChannelSettings[ChannelCount];
		for (var i = 0; i < channels.Length; i++)
		{
			channels[i] = new ChannelSettings();
		}

		return channels;
	}
}
