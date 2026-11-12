using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.Terminal;

/// <summary>A part of the screen.</summary>
public enum LayoutPart
{
	None,
	Header,
	Plot,
	Panel,
}

/// <summary>What <see cref="TerminalLayout.HitTest"/> found at a cell.</summary>
/// <param name="Part">Header, plot, panel or nothing.</param>
/// <param name="Slot">The index in <see cref="TerminalLayout.Slots"/>; -1 outside the graphs.</param>
/// <param name="Fraction">For a plot: how far across, 0 at the left edge to 1 at the right; NaN otherwise.</param>
public readonly record struct LayoutHit(LayoutPart Part, int Slot, double Fraction);

/// <summary>Where one graph goes on screen: its header row and the rows of its plot (0-based).</summary>
/// <param name="Index">The position of the graph in the list given to <see cref="TerminalLayout.Compute"/>.</param>
public readonly record struct GraphSlot(MetricKind Kind, int HeaderRow, int PlotRow, int PlotRows, int Index = 0);

/// <summary>
/// Stacks the graphs in the terminal: one header row each, the plots sharing the remaining rows by weight
/// (tall counts 2.5, collapsed 0), a blank row between graphs and a footer on the last row. On a small
/// terminal the gaps go first, then plots shrink to one row, then graphs that do not fit are left out.
/// An info panel, when asked for, takes a column on the right over the same rows as the graphs; it is left
/// out when the graphs would get fewer than <see cref="MinimumGraphWidth"/> columns.
/// </summary>
public sealed class TerminalLayout
{
	/// <summary>Narrowest the graphs may become to make room for the info panel.</summary>
	public const int MinimumGraphWidth = 60;

	/// <summary>Columns between the graphs and the info panel, and between the two columns of a wide panel.</summary>
	public const int PanelGap = 3;

	/// <summary>A panel narrower than this is left out.</summary>
	public const int MinimumPanelWidth = 30;

	/// <summary>A panel of one column never takes more than this: wider, labels and values drift apart.</summary>
	public const int MaximumPanelWidth = 50;

	/// <summary>From this terminal width on, the panel has two columns.</summary>
	public const int TwoColumnTerminalWidth = 220;

	/// <summary>A panel this wide or wider has two columns (one column stays below <see cref="MaximumPanelWidth"/>).</summary>
	public const int TwoColumnPanelWidth = (2 * 34) + PanelGap;

	/// <summary>
	/// The width of the info panel for a terminal of <paramref name="columns"/>: about a quarter of it in one column of 32
	/// to 50; from <see cref="TwoColumnTerminalWidth"/> on, a third of it in two columns of 34 to 44. Never more than
	/// leaves the graphs <see cref="MinimumGraphWidth"/>; 0 when there is no room for a panel.
	/// </summary>
	public static int PanelWidthFor(int columns, bool compact)
	{
		var left = Math.Min(compact ? 1 : 2, Math.Max(1, columns) / 8);
		var room = columns - (2 * left) - PanelGap - MinimumGraphWidth;
		var wanted = columns >= TwoColumnTerminalWidth
			? (2 * Math.Clamp(((columns / 3) - PanelGap) / 2, 34, 44)) + PanelGap
			: Math.Clamp(columns * 27 / 100, 32, MaximumPanelWidth);
		var width = Math.Min(wanted, room);
		if (width >= TwoColumnPanelWidth || width <= MaximumPanelWidth)
		{
			return width >= MinimumPanelWidth ? width : 0;
		}

		return MaximumPanelWidth; // Too narrow for two good columns: one column.
	}

	/// <summary>One column, or two in a panel of <see cref="TwoColumnPanelWidth"/> or more.</summary>
	public static int PanelColumnsFor(int panelWidth) => panelWidth >= TwoColumnPanelWidth ? 2 : 1;

	/// <summary>
	/// What is at a cell: the header or the plot of a graph (with how far across the plot, 0 at the left edge to 1 at the
	/// right), the info panel, or nothing.
	/// </summary>
	public LayoutHit HitTest(int column, int row)
	{
		if (PanelWidth > 0 && column >= PanelLeft && column < PanelLeft + PanelWidth && row >= PanelTop && row < PanelTop + PanelRows)
		{
			return new LayoutHit(LayoutPart.Panel, -1, double.NaN);
		}

		if (column < Left || column >= Left + Width)
		{
			return new LayoutHit(LayoutPart.None, -1, double.NaN);
		}

		for (var i = 0; i < Slots.Count; i++)
		{
			var slot = Slots[i];
			if (row == slot.HeaderRow)
			{
				return new LayoutHit(LayoutPart.Header, i, double.NaN);
			}

			if (row >= slot.PlotRow && row < slot.PlotRow + slot.PlotRows)
			{
				return new LayoutHit(LayoutPart.Plot, i, (column - Left + 0.5) / Width);
			}
		}

		return new LayoutHit(LayoutPart.None, -1, double.NaN);
	}

	/// <summary>First column of the info panel; 0 when there is no panel.</summary>
	public int PanelLeft { get; init; }

	/// <summary>Width of the info panel; 0 when there is no panel.</summary>
	public int PanelWidth { get; init; }

	/// <summary>First row of the info panel.</summary>
	public int PanelTop { get; init; }

	/// <summary>Rows of the info panel: the same rows the graphs use.</summary>
	public int PanelRows { get; init; }

	public required int Columns { get; init; }

	public required int Rows { get; init; }

	/// <summary>First column of the content.</summary>
	public required int Left { get; init; }

	/// <summary>Content width in columns.</summary>
	public required int Width { get; init; }

	public required int FooterRow { get; init; }

	public required IReadOnlyList<GraphSlot> Slots { get; init; }

	public static TerminalLayout Compute(int columns, int rows, IReadOnlyList<(MetricKind Kind, ChannelSize Size)> graphs, bool compact, int panelWidth = 0)
	{
		columns = Math.Max(1, columns);
		rows = Math.Max(1, rows);
		var left = Math.Min(compact ? 1 : 2, columns / 8);
		var graphWidth = Math.Max(1, columns - (2 * left));
		var panelLeft = 0;
		if (panelWidth > 0 && graphWidth - panelWidth - PanelGap >= MinimumGraphWidth)
		{
			graphWidth -= panelWidth + PanelGap;
			panelLeft = left + graphWidth + PanelGap;
		}
		else
		{
			panelWidth = 0;
		}
		var top = compact || rows < 12 ? 0 : 1;
		var footerRow = rows - 1;
		var bottom = footerRow - (compact || rows < 12 ? 0 : 1); // exclusive: a blank row above the footer
		var available = Math.Max(0, bottom - top);

		// Keep only the graphs whose header fits, then decide whether there is room for gaps between them.
		var count = Math.Min(graphs.Count, available);
		var gap = compact ? 0 : 1;
		if (count + ((count - 1) * gap) + count > available)
		{
			gap = 0;
		}

		var budget = Math.Max(0, available - count - (Math.Max(0, count - 1) * gap));
		var weights = new double[count];
		var totalWeight = 0.0;
		for (var i = 0; i < count; i++)
		{
			weights[i] = graphs[i].Size switch
			{
				ChannelSize.Tall => 2.5,
				ChannelSize.Collapsed => 0,
				_ => 1,
			};
			totalWeight += weights[i];
		}

		var plotRows = new int[count];
		var assigned = 0;
		for (var i = 0; i < count && totalWeight > 0; i++)
		{
			plotRows[i] = (int) Math.Floor(budget * weights[i] / totalWeight);
			assigned += plotRows[i];
		}

		for (var i = 0; assigned < budget && i < count; i = (i + 1) % count)
		{
			if (weights[i] > 0)
			{
				plotRows[i]++;
				assigned++;
			}
			else if (totalWeight == 0)
			{
				break;
			}
		}

		var slots = new List<GraphSlot>(count);
		var row = top;
		for (var i = 0; i < count; i++)
		{
			slots.Add(new GraphSlot(graphs[i].Kind, row, row + 1, plotRows[i], i));
			row += 1 + plotRows[i] + gap;
		}

		return new TerminalLayout
		{
			Columns = columns,
			Rows = rows,
			Left = left,
			Width = graphWidth,
			FooterRow = footerRow,
			Slots = slots,
			PanelLeft = panelLeft,
			PanelWidth = panelWidth,
			PanelTop = top,
			PanelRows = panelWidth > 0 ? available : 0,
		};
	}
}
