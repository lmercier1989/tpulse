using System.Windows.Media;

using Pulse.Core.Processing;

namespace Pulse.Rendering;

/// <summary>The instrument colours. Grid colours are white at low alpha, so they sit just above the background.</summary>
internal static class Palette
{
	public static readonly Color Background = Color.FromRgb(0x09, 0x0B, 0x0F);
	public static readonly Color TitleText = Color.FromRgb(0x87, 0x90, 0xA0);
	public static readonly Color WindowBorder = Color.FromRgb(0x1A, 0x1E, 0x25);

	public static readonly Color GridLine = Color.FromArgb(0x13, 0xFF, 0xFF, 0xFF);
	public static readonly Color GridDivision = Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF);
	public static readonly Color Baseline = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);

	public static readonly Color Cpu = Color.FromRgb(0x35, 0xC8, 0xE8);
	public static readonly Color Gpu = Color.FromRgb(0xA7, 0x8B, 0xFA);
	public static readonly Color Memory = Color.FromRgb(0x65, 0xD6, 0xA0);
	public static readonly Color Disk = Color.FromRgb(0xF2, 0xB8, 0x66);
	public static readonly Color Network = Color.FromRgb(0x65, 0x9E, 0xFF);
	public static readonly Color Claude = Color.FromRgb(0xD9, 0x77, 0x57);
	public static readonly Color Process = Color.FromRgb(0xE8, 0x79, 0xF9);
	public static readonly Color Counter = Color.FromRgb(0xA3, 0xE6, 0x35);
	public static readonly Color Git = Color.FromRgb(0xCB, 0xD5, 0xE1);
	public static readonly Color Warning = Color.FromRgb(0xF0, 0x71, 0x78);

	/// <summary>The colour of an event marker, the same as in the terminal: git, a Windows error, a mark, a spike, a leak.</summary>
	public static Color MarkerOf(Core.Metrics.EventKind kind)
	{
		var color = Core.Terminal.ScopeColors.MarkerOf(kind);
		return Color.FromRgb(color.R, color.G, color.B);
	}

	public static Color For(MetricKind kind) => kind switch
	{
		MetricKind.Cpu => Cpu,
		MetricKind.Gpu => Gpu,
		MetricKind.Memory => Memory,
		MetricKind.Disk => Disk,
		MetricKind.Network => Network,
		MetricKind.Process => Process,
		MetricKind.Counter => Counter,
		MetricKind.Git => Git,
		_ => Claude,
	};

	/// <summary>Mixes <paramref name="color"/> toward white; used for the second trace of a graph.</summary>
	public static Color Tint(Color color, double amount) => Color.FromRgb(
		(byte) Math.Round(color.R + ((255 - color.R) * amount)),
		(byte) Math.Round(color.G + ((255 - color.G) * amount)),
		(byte) Math.Round(color.B + ((255 - color.B) * amount)));

	public static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte) Math.Round(255 * alpha), color.R, color.G, color.B);
}
