namespace Pulse.Core.Terminal;

/// <summary>
/// A small software rasterizer for one graph, writing palette registers. Traces are sampled once per pixel
/// column and drawn as anti-aliased strokes: every pixel near the trace gets a coverage from its distance to
/// the polyline through the samples, quantized to the palette's levels. Where marks overlap, the most opaque
/// one wins, so a faint anti-aliasing edge never darkens the fill or a solid trace beneath it.
/// </summary>
public sealed class ScopeRaster(ScopePalette palette)
{
	private byte[] _pixels = [];

	public int Width { get; private set; }

	public int Height { get; private set; }

	public ReadOnlySpan<byte> Pixels => _pixels.AsSpan(0, Width * Height);

	/// <summary>Sets the size and clears to the background.</summary>
	public void Reset(int width, int height)
	{
		Width = Math.Max(0, width);
		Height = Math.Max(0, height);
		if (_pixels.Length < Width * Height)
		{
			_pixels = new byte[Width * Height];
		}

		_pixels.AsSpan(0, Width * Height).Clear();
	}

	/// <summary>A fixed graticule: four horizontal divisions (the bottom one brighter) and ten across.</summary>
	public void DrawGrid(double top, double bottom, double right)
	{
		var lastRow = (int) Math.Round(bottom);
		for (var i = 1; i < 10; i++)
		{
			var x = (int) Math.Round(right * i / 10);
			for (var y = (int) Math.Round(top); y <= lastRow; y++)
			{
				Plot(x, y, ScopePalette.GridDivision);
			}
		}

		for (var i = 0; i <= 4; i++)
		{
			var y = (int) Math.Round(top + ((bottom - top) * i / 4));
			var register = i == 4 ? ScopePalette.Baseline : ScopePalette.GridLine;
			for (var x = 0; x < (int) Math.Ceiling(right); x++)
			{
				Plot(x, y, register);
			}
		}
	}

	/// <summary>
	/// The gradient under a trace: strongest at the top of the plot, fading to nothing at zero, like the window.
	/// <paramref name="ys"/> holds the trace y at x = 0, 1, 2, ... (NaN in gaps).
	/// </summary>
	public void DrawFill(ReadOnlySpan<double> ys, double top, double bottom, byte firstRegister, int levels, double maxAlpha)
	{
		var columns = Math.Min(Width, ys.Length - 1);
		var lastRow = Math.Min(Height - 1, (int) Math.Ceiling(bottom));
		for (var x = 0; x < columns; x++)
		{
			var y = Middle(ys[x], ys[x + 1]);
			if (double.IsNaN(y))
			{
				continue;
			}

			var first = Math.Max(0, (int) Math.Floor(y));
			for (var row = first; row <= lastRow; row++)
			{
				var coverage = row == first ? Math.Clamp(row + 1 - y, 0, 1) : 1;
				var alpha = FillGradient((row + 0.5 - top) / (bottom - top)) * coverage;
				var level = (int) Math.Round(alpha / maxAlpha * levels);
				if (level > 0)
				{
					Plot(x, row, firstRegister + Math.Min(level, levels) - 1);
				}
			}
		}
	}

	/// <summary>
	/// An anti-aliased stroke of <paramref name="thickness"/> pixels through the samples in <paramref name="ys"/>
	/// (y at x = 0, 1, 2, ...; NaN breaks the stroke). Registers <paramref name="firstRegister"/> onward hold
	/// the coverage levels from faint to solid.
	/// </summary>
	public void DrawLine(ReadOnlySpan<double> ys, double thickness, byte firstRegister, int levels)
	{
		var half = thickness / 2;
		var columns = Math.Min(Width, ys.Length - 1);
		for (var x = 0; x < columns; x++)
		{
			// The segments that can reach this column: the one across it and its two neighbours.
			var minY = double.PositiveInfinity;
			var maxY = double.NegativeInfinity;
			for (var k = x - 1; k <= x + 1; k++)
			{
				if (IsSegment(ys, k))
				{
					minY = Math.Min(minY, Math.Min(ys[k], ys[k + 1]));
					maxY = Math.Max(maxY, Math.Max(ys[k], ys[k + 1]));
				}
			}

			if (double.IsPositiveInfinity(minY))
			{
				continue;
			}

			var centerX = x + 0.5;
			var firstRow = Math.Max(0, (int) Math.Floor(minY - half - 1));
			var lastRow = Math.Min(Height - 1, (int) Math.Ceiling(maxY + half + 1));
			for (var row = firstRow; row <= lastRow; row++)
			{
				var centerY = row + 0.5;
				var distance = double.PositiveInfinity;
				for (var k = x - 1; k <= x + 1; k++)
				{
					if (IsSegment(ys, k))
					{
						distance = Math.Min(distance, DistanceToSegment(centerX, centerY, k, ys[k], k + 1, ys[k + 1]));
					}
				}

				var level = (int) Math.Round(Math.Clamp(half + 0.5 - distance, 0, 1) * levels);
				if (level > 0)
				{
					Plot(x, row, firstRegister + level - 1);
				}
			}
		}
	}

	/// <summary>An anti-aliased disc: the beam dot where a trace meets the right edge.</summary>
	public void DrawDot(double centerX, double centerY, double radius, byte firstRegister, int levels)
	{
		var top = (int) Math.Floor(centerY - radius - 1);
		var bottom = (int) Math.Ceiling(centerY + radius + 1);
		var left = (int) Math.Floor(centerX - radius - 1);
		var right = (int) Math.Ceiling(centerX + radius + 1);
		for (var y = top; y <= bottom; y++)
		{
			for (var x = left; x <= right; x++)
			{
				var distance = Math.Sqrt(Square(x + 0.5 - centerX) + Square(y + 0.5 - centerY));
				var level = (int) Math.Round(Math.Clamp(radius + 0.5 - distance, 0, 1) * levels);
				if (level > 0)
				{
					Plot(x, y, firstRegister + level - 1);
				}
			}
		}
	}

	/// <summary>
	/// An event marker: a dashed vertical line at <paramref name="x"/> from <paramref name="top"/> to
	/// <paramref name="bottom"/>, drawn before the traces so they stay on top of it.
	/// </summary>
	public void DrawMarker(double x, double top, double bottom, byte register)
	{
		var column = (int) Math.Round(x);
		for (var y = (int) Math.Round(top); y <= (int) Math.Round(bottom); y++)
		{
			if (y % 6 < 4)
			{
				Plot(column, y, register);
			}
		}
	}

	/// <summary>The mouse cursor: a solid vertical line at <paramref name="x"/>, drawn under the traces like a marker.</summary>
	public void DrawCursor(double x, double top, double bottom, byte register)
	{
		var column = (int) Math.Round(x);
		for (var y = (int) Math.Round(top); y <= (int) Math.Round(bottom); y++)
		{
			Plot(column, y, register);
		}
	}

	/// <summary>
	/// Lights the columns from <paramref name="x0"/> to <paramref name="x1"/> between <paramref name="top"/> and
	/// <paramref name="bottom"/>: a selected range of time, faint enough that everything else stays on top of it.
	/// </summary>
	public void DrawBand(double x0, double x1, double top, double bottom, byte register)
	{
		var first = Math.Max(0, (int) Math.Round(Math.Min(x0, x1)));
		var last = Math.Min(Width - 1, (int) Math.Round(Math.Max(x0, x1)));
		for (var x = first; x <= last; x++)
		{
			for (var y = (int) Math.Round(top); y <= (int) Math.Round(bottom); y++)
			{
				Plot(x, y, register);
			}
		}
	}

	/// <summary>
	/// One band of a stacked graph in pixel column <paramref name="x"/>: filled from <paramref name="yTop"/> down to
	/// <paramref name="yBottom"/>, with a line of <paramref name="edge"/> along its top.
	/// </summary>
	public void DrawStackColumn(int x, double yTop, double yBottom, byte fill, byte edge)
	{
		var first = (int) Math.Round(yTop);
		var last = (int) Math.Round(yBottom) - 1;
		if (last < first)
		{
			return;
		}

		Plot(x, first, edge);
		for (var y = first + 1; y <= last; y++)
		{
			Plot(x, y, fill);
		}
	}

	/// <summary>The fill opacity at <paramref name="t"/> (0 at the top of the plot, 1 at zero).</summary>
	internal static double FillGradient(double t)
	{
		t = Math.Clamp(t, 0, 1);
		return t < 0.55 ? 0.20 + ((0.07 - 0.20) * t / 0.55) : 0.07 * (1 - ((t - 0.55) / 0.45));
	}

	private static bool IsSegment(ReadOnlySpan<double> ys, int k)
		=> k >= 0 && k + 1 < ys.Length && double.IsFinite(ys[k]) && double.IsFinite(ys[k + 1]);

	private static double Middle(double a, double b)
		=> double.IsFinite(a) && double.IsFinite(b) ? (a + b) / 2 : double.IsFinite(a) ? a : double.IsFinite(b) ? b : double.NaN;

	private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
	{
		var dx = bx - ax;
		var dy = by - ay;
		var t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
		return Math.Sqrt(Square(px - (ax + (t * dx))) + Square(py - (ay + (t * dy))));
	}

	private static double Square(double value) => value * value;

	private void Plot(int x, int y, int register)
	{
		if ((uint) x >= (uint) Width || (uint) y >= (uint) Height)
		{
			return;
		}

		ref var pixel = ref _pixels[(y * Width) + x];
		if (palette.AlphaOf((byte) register) >= palette.AlphaOf(pixel))
		{
			pixel = (byte) register;
		}
	}
}
