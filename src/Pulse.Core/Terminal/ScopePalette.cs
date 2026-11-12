using Pulse.Core.Processing;

namespace Pulse.Core.Terminal;

/// <summary>A 24-bit colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
	public static Rgb FromHex(int hex) => new((byte) (hex >> 16), (byte) (hex >> 8), (byte) hex);

	/// <summary>Linear mix from <paramref name="from"/> (0) to <paramref name="to"/> (1).</summary>
	public static Rgb Lerp(Rgb from, Rgb to, double amount) => new(Mix(from.R, to.R, amount), Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));

	private static byte Mix(byte from, byte to, double amount) => (byte) Math.Round(from + ((to - from) * Math.Clamp(amount, 0, 1)));
}

/// <summary>The instrument colours, the same as the window's.</summary>
public static class ScopeColors
{
	public static readonly Rgb Background = Rgb.FromHex(0x090B0F);
	public static readonly Rgb TextPrimary = Rgb.FromHex(0xE4E8EE);
	public static readonly Rgb TextSecondary = Rgb.FromHex(0x8790A0);
	public static readonly Rgb TextDim = Rgb.FromHex(0x525B68);
	public static readonly Rgb White = new(255, 255, 255);

	public static Rgb For(MetricKind kind) => kind switch
	{
		MetricKind.Cpu => Rgb.FromHex(0x35C8E8),
		MetricKind.Gpu => Rgb.FromHex(0xA78BFA),
		MetricKind.Memory => Rgb.FromHex(0x65D6A0),
		MetricKind.Disk => Rgb.FromHex(0xF2B866),
		MetricKind.Network => Rgb.FromHex(0x659EFF),
		MetricKind.Process => Rgb.FromHex(0xE879F9),
		MetricKind.Counter => Rgb.FromHex(0xA3E635),
		MetricKind.Git => Rgb.FromHex(0xCBD5E1),
		_ => Rgb.FromHex(0xD97757),
	};

	/// <summary>Values and gauges past a limit: a disk 90 % full, a load that stays at 90 % or more.</summary>
	public static readonly Rgb Warning = Rgb.FromHex(0xF07178);

	/// <summary>The lighter tint of the second trace (write, upload, VRAM).</summary>
	public static Rgb SecondaryOf(MetricKind kind) => Rgb.Lerp(For(kind), White, 0.5);

	/// <summary><paramref name="color"/> at <paramref name="alpha"/> over the background.</summary>
	public static Rgb Over(Rgb color, double alpha) => Rgb.Lerp(Background, color, alpha);

	/// <summary>
	/// The colour of an event marker, by what it means: what your tools did (git) in the git colour, what you set
	/// yourself (a mark) in the colour of a selection, what Pulse found in orange (a spike) or pink (a leak), and an
	/// error in the warning colour.
	/// </summary>
	public static Rgb MarkerOf(Metrics.EventKind kind) => kind switch
	{
		Metrics.EventKind.Error => Warning,
		Metrics.EventKind.Mark => Selection,
		Metrics.EventKind.Spike => Rgb.FromHex(0xFB923C),
		Metrics.EventKind.Leak => Rgb.FromHex(0xF472B6),
		_ => For(MetricKind.Git),
	};

	/// <summary>What you set yourself: the edges of a selected range of time, and a mark.</summary>
	public static readonly Rgb Selection = Rgb.FromHex(0x7DD3FC);

	/// <summary>The trace of the baseline, drawn faintly behind the live trace.</summary>
	public static readonly Rgb Ghost = Rgb.FromHex(0xE2E8F0);

	/// <summary>The bands of the stacked CPU graph: the five busiest processes, then everything else.</summary>
	public const int StackBands = 6;

	private static readonly Rgb[] StackColors =
	[
		Rgb.FromHex(0x60A5FA),
		Rgb.FromHex(0xA78BFA),
		Rgb.FromHex(0x2DD4BF),
		Rgb.FromHex(0xE879F9),
		Rgb.FromHex(0xA3E635),
		Rgb.FromHex(0x475569),
	];

	/// <summary>The colour of band <paramref name="index"/> of the stacked CPU graph; the last one is everything else.</summary>
	public static Rgb Stack(int index) => StackColors[Math.Clamp(index, 0, StackBands - 1)];
}

/// <summary>
/// The colour registers of a Sixel image. Anti-aliasing and the gradient fill are expressed as a few levels
/// of each colour blended over the background, so a whole frame needs about 130 registers (Sixel allows 256).
/// Every register also knows its opacity, which the rasterizer uses to keep the strongest mark in a pixel.
/// </summary>
public sealed class ScopePalette
{
	public const int StrokeLevels = 6;
	public const int FillLevels = 10;
	public const int CoreLevels = 3;
	public const double FillAlpha = 0.22;
	public const double SecondaryAlpha = 0.9;
	public const double CoreAlpha = 0.32;

	/// <summary>Event markers sit above the fill and the grid, below every trace.</summary>
	public const double MarkerAlpha = 0.6;

	/// <summary>The mouse cursor line: like a marker, a little brighter.</summary>
	public const double CursorAlpha = 0.7;

	/// <summary>The selected range is lit just enough to tell it apart, below the fill, the grid and every trace.</summary>
	public const double SelectionAlpha = 0.05;

	/// <summary>The baseline trace, at two anti-aliasing levels up to this opacity.</summary>
	public const double GhostAlpha = 0.4;

	public const int GhostLevels = 2;

	/// <summary>The bands of the stacked CPU graph, and the line along the top of each.</summary>
	public const double StackAlpha = 0.5;

	public const double StackEdgeAlpha = 0.85;

	/// <summary>The register of the mouse cursor line, after the markers.</summary>
	public static byte Cursor => (byte) (Marker(Enum.GetValues<Metrics.EventKind>()[^1]) + 1);

	/// <summary>The selected range of time.</summary>
	public static byte SelectionBand => (byte) (Cursor + 1);

	/// <summary>The two edges of the selected range.</summary>
	public static byte SelectionEdge => (byte) (Cursor + 2);

	/// <summary>The baseline trace at anti-aliasing level 1 to <see cref="GhostLevels"/>.</summary>
	public static byte Ghost(int level) => (byte) (Cursor + 3 + Math.Clamp(level, 1, GhostLevels) - 1);

	/// <summary>Band <paramref name="index"/> of the stacked CPU graph.</summary>
	public static byte StackFill(int index) => (byte) (Ghost(GhostLevels) + 1 + Math.Clamp(index, 0, ScopeColors.StackBands - 1));

	/// <summary>The line along the top of band <paramref name="index"/>.</summary>
	public static byte StackEdge(int index) => (byte) (StackFill(ScopeColors.StackBands - 1) + 1 + Math.Clamp(index, 0, ScopeColors.StackBands - 1));

	public const byte Background = 0;
	public const byte GridDivision = 1;
	public const byte GridLine = 2;
	public const byte Baseline = 3;

	private const int FirstKindRegister = 4;
	private const int RegistersPerKind = (2 * StrokeLevels) + FillLevels + CoreLevels;

	private readonly Rgb[] _colors;
	private readonly double[] _alpha;

	private ScopePalette()
	{
		var kinds = Enum.GetValues<MetricKind>();
		var markers = Enum.GetValues<Metrics.EventKind>();
		var count = StackEdge(ScopeColors.StackBands - 1) + 1;
		_colors = new Rgb[count];
		_alpha = new double[count];

		Set(Background, ScopeColors.Background, 0);
		Set(GridDivision, ScopeColors.Over(ScopeColors.White, 0.045), 0.045);
		Set(GridLine, ScopeColors.Over(ScopeColors.White, 0.075), 0.075);
		Set(Baseline, ScopeColors.Over(ScopeColors.White, 0.14), 0.14);
		foreach (var marker in markers)
		{
			Set(Marker(marker), ScopeColors.Over(ScopeColors.MarkerOf(marker), MarkerAlpha), MarkerAlpha);
		}

		Set(Cursor, ScopeColors.Over(ScopeColors.White, CursorAlpha), CursorAlpha);
		Set(SelectionBand, ScopeColors.Over(ScopeColors.White, SelectionAlpha), SelectionAlpha);
		Set(SelectionEdge, ScopeColors.Over(ScopeColors.Selection, CursorAlpha), CursorAlpha);
		for (var level = 1; level <= GhostLevels; level++)
		{
			var alpha = GhostAlpha * level / GhostLevels;
			Set(Ghost(level), ScopeColors.Over(ScopeColors.Ghost, alpha), alpha);
		}

		for (var band = 0; band < ScopeColors.StackBands; band++)
		{
			Set(StackFill(band), ScopeColors.Over(ScopeColors.Stack(band), StackAlpha), StackAlpha);
			Set(StackEdge(band), ScopeColors.Over(ScopeColors.Stack(band), StackEdgeAlpha), StackEdgeAlpha);
		}

		foreach (var kind in kinds)
		{
			var primary = ScopeColors.For(kind);
			var secondary = ScopeColors.SecondaryOf(kind);
			for (var level = 1; level <= StrokeLevels; level++)
			{
				var alpha = (double) level / StrokeLevels;
				Set(Stroke(kind, level), ScopeColors.Over(primary, alpha), alpha);
				Set(SecondaryStroke(kind, level), ScopeColors.Over(secondary, alpha * SecondaryAlpha), alpha * SecondaryAlpha);
			}

			for (var level = 1; level <= FillLevels; level++)
			{
				var alpha = FillAlpha * level / FillLevels;
				Set(Fill(kind, level), ScopeColors.Over(primary, alpha), alpha);
			}

			for (var level = 1; level <= CoreLevels; level++)
			{
				var alpha = CoreAlpha * level / CoreLevels;
				Set(Core(kind, level), ScopeColors.Over(primary, alpha), alpha);
			}
		}
	}

	public static ScopePalette Instance { get; } = new();

	public int Count => _colors.Length;

	public Rgb this[int register] => _colors[register];

	public double AlphaOf(byte register) => _alpha[register];

	/// <summary>The primary trace at anti-aliasing level 1 (faint) to <see cref="StrokeLevels"/> (solid).</summary>
	public static byte Stroke(MetricKind kind, int level) => Register(kind, 0, level, StrokeLevels);

	public static byte SecondaryStroke(MetricKind kind, int level) => Register(kind, StrokeLevels, level, StrokeLevels);

	public static byte Fill(MetricKind kind, int level) => Register(kind, 2 * StrokeLevels, level, FillLevels);

	public static byte Core(MetricKind kind, int level) => Register(kind, (2 * StrokeLevels) + FillLevels, level, CoreLevels);

	/// <summary>The register of an event marker, after the registers of every graph.</summary>
	public static byte Marker(Metrics.EventKind kind)
		=> (byte) (FirstKindRegister + (Enum.GetValues<MetricKind>().Length * RegistersPerKind) + (int) kind);

	private static byte Register(MetricKind kind, int offset, int level, int levels)
		=> (byte) (FirstKindRegister + ((int) kind * RegistersPerKind) + offset + Math.Clamp(level, 1, levels) - 1);

	private void Set(byte register, Rgb color, double alpha)
	{
		_colors[register] = color;
		_alpha[register] = alpha;
	}
}
