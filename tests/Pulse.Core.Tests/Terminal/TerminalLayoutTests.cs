using Pulse.Core.Processing;
using Pulse.Core.Settings;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

public sealed class TerminalLayoutTests
{
	private static readonly (MetricKind, ChannelSize)[] AllNormal =
		[.. MetricKinds.Builtin.Select(kind => (kind, ChannelSize.Normal))];

	[Fact]
	public void Compute_AllGraphs_ShareTheRowsEvenly()
	{
		// Act
		var layout = TerminalLayout.Compute(120, 40, AllNormal, compact: false);

		// Assert
		Assert.Equal(AllNormal.Length, layout.Slots.Count);
		Assert.Equal(39, layout.FooterRow);
		Assert.Equal(2, layout.Left);
		Assert.Equal(116, layout.Width);
		Assert.InRange(layout.Slots.Max(slot => slot.PlotRows) - layout.Slots.Min(slot => slot.PlotRows), 0, 1);
	}

	[Fact]
	public void Compute_TallAndCollapsed_GetMoreAndNoPlotRows()
	{
		// Arrange
		(MetricKind, ChannelSize)[] graphs = [(MetricKind.Cpu, ChannelSize.Tall), (MetricKind.Memory, ChannelSize.Normal), (MetricKind.Disk, ChannelSize.Collapsed)];

		// Act
		var layout = TerminalLayout.Compute(100, 40, graphs, compact: false);

		// Assert
		Assert.True(layout.Slots[0].PlotRows > 2 * layout.Slots[1].PlotRows);
		Assert.Equal(0, layout.Slots[2].PlotRows);
	}

	[Fact]
	public void Compute_SmallTerminal_KeepsWhatFits()
	{
		// Act
		var layout = TerminalLayout.Compute(60, 8, AllNormal, compact: false);

		// Assert
		Assert.True(layout.Slots.Count <= 7);
		Assert.All(layout.Slots, slot => Assert.True(slot.PlotRow + slot.PlotRows <= layout.FooterRow));
	}

	[Fact]
	public void Compute_WithPanel_PutsItRightOfTheGraphsOverTheSameRows()
	{
		// Act
		var layout = TerminalLayout.Compute(150, 46, AllNormal, compact: false, panelWidth: 36);

		// Assert
		Assert.Equal(36, layout.PanelWidth);
		Assert.Equal(150 - 2 - 36, layout.PanelLeft);
		Assert.Equal(layout.PanelLeft - TerminalLayout.PanelGap, layout.Left + layout.Width);
		Assert.Equal(layout.Slots[0].HeaderRow, layout.PanelTop);
		Assert.True(layout.PanelTop + layout.PanelRows <= layout.FooterRow);
	}

	[Fact]
	public void Compute_PanelOnNarrowTerminal_IsLeftOut()
	{
		// Act
		var layout = TerminalLayout.Compute(90, 40, AllNormal, compact: false, panelWidth: 30);

		// Assert
		Assert.Equal(0, layout.PanelWidth);
		Assert.Equal(0, layout.PanelRows);
		Assert.Equal(86, layout.Width);
	}

	[Fact]
	public void Compute_PanelAtAnySize_NeverOverlapsTheGraphsOrTheFooter()
	{
		for (var columns = 40; columns <= 260; columns += 13)
		{
			for (var rows = 1; rows <= 60; rows += 7)
			{
				// Act
				var layout = TerminalLayout.Compute(columns, rows, AllNormal, compact: false, panelWidth: Math.Clamp(columns / 4, 30, 40));

				// Assert
				if (layout.PanelWidth > 0)
				{
					Assert.True(layout.Left + layout.Width + TerminalLayout.PanelGap <= layout.PanelLeft);
					Assert.True(layout.PanelLeft + layout.PanelWidth <= columns);
					Assert.True(layout.Width >= TerminalLayout.MinimumGraphWidth);
					Assert.True(layout.PanelTop + layout.PanelRows <= layout.FooterRow);
				}
			}
		}
	}

	[Fact]
	public void Compute_NoGraphs_HasNoSlots()
	{
		// Act
		var layout = TerminalLayout.Compute(80, 24, [], compact: false);

		// Assert
		Assert.Empty(layout.Slots);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Compute_AnySize_SlotsNeverOverlapOrReachTheFooter(bool compact)
	{
		for (var rows = 1; rows <= 70; rows++)
		{
			for (var columns = 10; columns <= 200; columns += 47)
			{
				// Act
				var layout = TerminalLayout.Compute(columns, rows, AllNormal, compact);

				// Assert
				var next = 0;
				foreach (var slot in layout.Slots)
				{
					Assert.True(slot.HeaderRow >= next);
					Assert.Equal(slot.HeaderRow + 1, slot.PlotRow);
					next = slot.PlotRow + slot.PlotRows;
				}

				Assert.True(next <= layout.FooterRow);
				Assert.True(layout.Left + layout.Width <= columns);
			}
		}
	}
}
