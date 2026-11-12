using Pulse.Core.Processing;

namespace Pulse.Core.Terminal;

/// <summary>Which trace a braille dot belongs to; the colour of a cell follows the most important one.</summary>
public enum BrailleLayer
{
	Primary,
	Secondary,
	Core,

	/// <summary>The baseline, faint behind the live traces.</summary>
	Ghost,
}

/// <summary>
/// The fallback renderer for terminals without Sixel: every character cell is a 2 x 4 grid of braille dots,
/// coloured with 24-bit colour. The gradient fill becomes the background colour of the cells under the trace,
/// so even this mode keeps the instrument look.
/// </summary>
public sealed class BrailleScope
{
	private static readonly byte[] LeftDots = [0x01, 0x02, 0x04, 0x40];
	private static readonly byte[] RightDots = [0x08, 0x10, 0x20, 0x80];

	private byte[] _primary = [];
	private byte[] _secondary = [];
	private byte[] _core = [];
	private byte[] _grid = [];
	private byte[] _marker = [];
	private Metrics.EventKind[] _markerKind = [];
	private double[] _fill = [];
	private byte[] _ghost = [];
	private bool[] _band = [];
	private sbyte[] _stack = [];

	public int Columns { get; private set; }

	public int Rows { get; private set; }

	/// <summary>Dots across: two per cell.</summary>
	public int DotWidth => Columns * 2;

	/// <summary>Dots down: four per cell.</summary>
	public int DotHeight => Rows * 4;

	public void Reset(int columns, int rows)
	{
		Columns = Math.Max(0, columns);
		Rows = Math.Max(0, rows);
		var cells = Columns * Rows;
		if (_primary.Length < cells)
		{
			_primary = new byte[cells];
			_secondary = new byte[cells];
			_core = new byte[cells];
			_grid = new byte[cells];
			_marker = new byte[cells];
			_markerKind = new Metrics.EventKind[cells];
			_fill = new double[cells];
			_ghost = new byte[cells];
			_band = new bool[cells];
			_stack = new sbyte[cells];
		}

		_ghost.AsSpan(0, cells).Clear();
		_band.AsSpan(0, cells).Clear();
		_stack.AsSpan(0, cells).Fill(-1);

		_marker.AsSpan(0, cells).Clear();

		_primary.AsSpan(0, cells).Clear();
		_secondary.AsSpan(0, cells).Clear();
		_core.AsSpan(0, cells).Clear();
		_grid.AsSpan(0, cells).Clear();
		_fill.AsSpan(0, cells).Clear();
	}

	/// <summary>Dotted lines at a quarter, half and three quarters of the scale, and a denser baseline.</summary>
	public void DrawGrid()
	{
		for (var i = 1; i <= 4; i++)
		{
			var y = (int) Math.Round((DotHeight - 1) * i / 4.0);
			var spacing = i == 4 ? 2 : 4;
			for (var x = 0; x < DotWidth; x += spacing)
			{
				Set(_grid, x, y);
			}
		}
	}

	/// <summary>An event marker: every other dot down the dot column <paramref name="x"/>, below the traces.</summary>
	public void DrawMarker(double x, Metrics.EventKind kind)
	{
		var column = (int) Math.Round(x);
		if ((uint) column >= (uint) DotWidth)
		{
			return;
		}

		for (var y = 0; y < DotHeight; y += 2)
		{
			Set(_marker, column, y);
			_markerKind[((y >> 2) * Columns) + (column >> 1)] = kind;
		}
	}

	/// <summary>Draws a trace given its y (in dots) at every dot column edge x = 0, 1, 2, ...; NaN breaks it.</summary>
	public void DrawLine(ReadOnlySpan<double> ys, BrailleLayer layer)
	{
		var target = layer switch
		{
			BrailleLayer.Primary => _primary,
			BrailleLayer.Secondary => _secondary,
			BrailleLayer.Ghost => _ghost,
			_ => _core,
		};

		var columns = Math.Min(DotWidth, ys.Length - 1);
		for (var x = 0; x < columns; x++)
		{
			var a = ys[x];
			var b = ys[x + 1];
			if (!double.IsFinite(a) && !double.IsFinite(b))
			{
				continue;
			}

			a = double.IsFinite(a) ? a : b;
			b = double.IsFinite(b) ? b : a;
			var first = Math.Clamp((int) Math.Round(Math.Min(a, b)), 0, DotHeight - 1);
			var last = Math.Clamp((int) Math.Round(Math.Max(a, b)), 0, DotHeight - 1);
			for (var y = first; y <= last; y++)
			{
				Set(target, x, y);
			}
		}
	}

	/// <summary>Gives every cell whose centre lies under the trace the gradient colour of its row.</summary>
	public void DrawFill(ReadOnlySpan<double> ys)
	{
		for (var column = 0; column < Columns; column++)
		{
			var center = (column * 2) + 1;
			if (center >= ys.Length || !double.IsFinite(ys[center]))
			{
				continue;
			}

			for (var row = 0; row < Rows; row++)
			{
				var centerY = (row * 4) + 2.0;
				if (centerY > ys[center])
				{
					_fill[(row * Columns) + column] = ScopeRaster.FillGradient(centerY / DotHeight);
				}
			}
		}
	}

	/// <summary>Lights the cells between dot columns <paramref name="x0"/> and <paramref name="x1"/>: a selected range of time.</summary>
	public void DrawBand(double x0, double x1)
	{
		var first = Math.Max(0, (int) Math.Round(Math.Min(x0, x1)) >> 1);
		var last = Math.Min(Columns - 1, (int) Math.Round(Math.Max(x0, x1)) >> 1);
		for (var column = first; column <= last; column++)
		{
			for (var row = 0; row < Rows; row++)
			{
				_band[(row * Columns) + column] = true;
			}
		}
	}

	/// <summary>Colours the cell at (<paramref name="column"/>, <paramref name="row"/>) with band <paramref name="band"/> of a stacked graph.</summary>
	public void SetStack(int column, int row, int band)
	{
		if ((uint) column < (uint) Columns && (uint) row < (uint) Rows)
		{
			_stack[(row * Columns) + column] = (sbyte) band;
		}
	}

	/// <summary>Writes the cells at (<paramref name="row"/>, <paramref name="column"/>), changing colours only where needed.</summary>
	public void WriteTo(AnsiWriter output, int row, int column, MetricKind kind)
	{
		var primary = ScopeColors.For(kind);
		var secondary = ScopeColors.SecondaryOf(kind);
		var core = ScopeColors.Over(primary, 0.5);
		var grid = ScopeColors.Over(ScopeColors.White, 0.16);
		var ghost = ScopeColors.Over(ScopeColors.Ghost, ScopePalette.GhostAlpha);
		var band = ScopeColors.Over(ScopeColors.White, 0.07);

		for (var y = 0; y < Rows; y++)
		{
			output.MoveTo(row + y, column);
			Rgb? foreground = null;
			Rgb? background = null;
			for (var x = 0; x < Columns; x++)
			{
				var cell = (y * Columns) + x;
				var dots = _primary[cell] | _secondary[cell] | _core[cell];
				var color = _primary[cell] != 0 ? primary : _secondary[cell] != 0 ? secondary : _core[cell] != 0 ? core : grid;
				if (dots == 0 && _ghost[cell] != 0)
				{
					dots = _ghost[cell];
					color = ghost;
				}

				if (dots == 0 && _marker[cell] != 0)
				{
					dots = _marker[cell];
					color = ScopeColors.Over(ScopeColors.MarkerOf(_markerKind[cell]), ScopePalette.MarkerAlpha);
				}

				if (dots == 0)
				{
					dots = _grid[cell];
				}

				var fill = _stack[cell] >= 0 ? ScopeColors.Over(ScopeColors.Stack(_stack[cell]), ScopePalette.StackAlpha)
					: _fill[cell] > 0 ? ScopeColors.Over(primary, _fill[cell])
					: _band[cell] ? band
					: ScopeColors.Background;
				if (background != fill)
				{
					output.Background(fill);
					background = fill;
				}

				if (dots == 0)
				{
					output.Byte(' ');
					continue;
				}

				if (foreground != color)
				{
					output.Foreground(color);
					foreground = color;
				}

				// U+2800 + dots, written as UTF-8 directly.
				output.Byte(0xE2).Byte((byte) (0xA0 + (dots >> 6))).Byte((byte) (0x80 + (dots & 0x3F)));
			}
		}
	}

	private void Set(byte[] layer, int x, int y)
	{
		if ((uint) x >= (uint) DotWidth || (uint) y >= (uint) DotHeight)
		{
			return;
		}

		layer[((y >> 2) * Columns) + (x >> 1)] |= ((x & 1) == 0 ? LeftDots : RightDots)[y & 3];
	}
}
