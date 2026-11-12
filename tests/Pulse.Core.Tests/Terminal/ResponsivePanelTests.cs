using System.Text;

using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Settings;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

/// <summary>The panel follows the terminal: its width, its columns, its label column, and what the mouse points at.</summary>
public sealed class ResponsivePanelTests
{
	[Theory]
	[InlineData(80, 0)] // The graphs need 60 columns: no room for a panel.
	[InlineData(100, 32)]
	[InlineData(150, 40)]
	[InlineData(200, 50)] // One column stops at 50.
	[InlineData(250, 83)] // Two columns of 40.
	[InlineData(400, 91)] // Two columns of 44.
	public void PanelWidthFor_GrowsWithTheTerminal_AndLeavesTheGraphsTheirRoom(int columns, int expected)
	{
		// Act & Assert
		Assert.Equal(expected, TerminalLayout.PanelWidthFor(columns, compact: false));
	}

	[Theory]
	[InlineData(50, 1)]
	[InlineData(70, 1)]
	[InlineData(71, 2)]
	public void PanelColumnsFor_TwoFromAWidePanel(int width, int expected)
	{
		// Act & Assert
		Assert.Equal(expected, TerminalLayout.PanelColumnsFor(width));
	}

	[Fact]
	public void FitColumns_BreaksBetweenSections_AndUsesBothColumns()
	{
		// Arrange: three sections of five rows.
		var lines = new List<PanelLine>();
		foreach (var section in new[] { "ONE", "TWO", "THREE" })
		{
			lines.Add(new PanelLine(PanelLineKind.Section, section, string.Empty, 0));
			for (var i = 0; i < 4; i++)
			{
				lines.Add(new PanelLine(PanelLineKind.Field, $"{section}{i}", "x", 1));
			}

			lines.Add(new PanelLine(PanelLineKind.Blank, string.Empty, string.Empty, 9));
		}

		// Act
		var columns = InfoPanel.FitColumns(lines, rows: 10, columns: 2);

		// Assert: every row kept, every column starting with a section, none taller than the rows.
		Assert.Equal(2, columns.Count);
		Assert.All(columns, column => Assert.True(column.Count <= 10));
		Assert.Equal(15, columns.Sum(column => column.Count(line => line.Kind != PanelLineKind.Blank)));
		Assert.All(columns.Where(column => column.Count > 0), column => Assert.Equal(PanelLineKind.Section, column[0].Kind));
	}

	[Fact]
	public void FitColumns_EverythingFits_StillEvensOutTheColumns()
	{
		// Arrange: two sections of three rows in a tall panel.
		PanelLine[] lines =
		[
			new(PanelLineKind.Section, "ONE", string.Empty, 0), new(PanelLineKind.Field, "a", "x", 1), new(PanelLineKind.Field, "b", "x", 1),
			new(PanelLineKind.Section, "TWO", string.Empty, 0), new(PanelLineKind.Field, "c", "x", 1), new(PanelLineKind.Field, "d", "x", 1),
		];

		// Act
		var columns = InfoPanel.FitColumns(lines, rows: 40, columns: 2);

		// Assert
		Assert.Equal([3, 3], columns.Select(column => column.Count));
	}

	[Fact]
	public void LabelWidths_EverySectionHasItsOwn()
	{
		// Arrange
		PanelLine[] rows =
		[
			new(PanelLineKind.Section, "SYSTEM", string.Empty, 0), new(PanelLineKind.Field, "host", "pc", 1),
			new(PanelLineKind.Section, "GIT", string.Empty, 0), new(PanelLineKind.Field, "Shop.Monitoring", "master", 1),
		];

		// Act
		var widths = InfoPanel.LabelWidths(rows, 40);

		// Assert
		Assert.Equal([8, 8, 16, 16], widths);
	}

	[Fact]
	public void LabelWidthFor_FitsTheLongestLabel_WithinLimits()
	{
		// Arrange
		PanelLine[] lines = [new(PanelLineKind.Field, "Webshop-v2", "main", 1), new(PanelLineKind.Field, "ipv4", "x", 1)];
		PanelLine[] long_ = [new(PanelLineKind.Field, "A.Very.Long.Repository.Name", "main", 1)];

		// Act & Assert
		Assert.Equal(12, InfoPanel.LabelWidthFor(lines, 40));
		Assert.Equal(16, InfoPanel.LabelWidthFor(long_, 60));
		Assert.Equal(13, InfoPanel.LabelWidthFor(long_, 30)); // At most about half the panel.
		Assert.Equal(8, InfoPanel.LabelWidthFor([], 40));
	}

	[Fact]
	public void WriteLine_RightAlignsTheValue_AndCutsTheTextFirst()
	{
		// Arrange
		var output = new AnsiWriter();
		var line = new PanelLine(PanelLineKind.Field, "Webshop-v2", "feat/checkout-and-payment-flow", 1, Right: "+9,6k −466");

		// Act
		PanelView.WriteLine(output, row: 0, left: 0, width: 40, line, labelWidth: 12);
		var text = Visible(output);

		// Assert
		Assert.StartsWith("Webshop-v2  feat/checkout-a…", text, StringComparison.Ordinal);
		Assert.EndsWith("+9,6k −466", text, StringComparison.Ordinal);
	}

	[Fact]
	public void WriteLine_Section_DrawsARuleToTheEdge()
	{
		// Arrange
		var output = new AnsiWriter();

		// Act
		PanelView.WriteLine(output, 0, 0, 20, new PanelLine(PanelLineKind.Section, "GIT", string.Empty, 0));

		// Assert
		Assert.Equal("GIT " + new string('─', 16), Visible(output));
	}

	[Fact]
	public void HitTest_FindsHeadersPlotsAndThePanel()
	{
		// Arrange
		var layout = TerminalLayout.Compute(150, 40, [(MetricKind.Cpu, ChannelSize.Normal), (MetricKind.Memory, ChannelSize.Normal)], compact: false, panelWidth: 40);
		var cpu = layout.Slots[0];

		// Act
		var header = layout.HitTest(layout.Left + 3, cpu.HeaderRow);
		var middle = layout.HitTest(layout.Left + (layout.Width / 2), cpu.PlotRow);
		var right = layout.HitTest(layout.Left + layout.Width - 1, cpu.PlotRow);
		var panel = layout.HitTest(layout.PanelLeft + 1, layout.PanelTop);

		// Assert
		Assert.Equal((LayoutPart.Header, 0), (header.Part, header.Slot));
		Assert.Equal(LayoutPart.Plot, middle.Part);
		Assert.InRange(middle.Fraction, 0.49, 0.51);
		Assert.InRange(right.Fraction, 0.99, 1);
		Assert.Equal(LayoutPart.Panel, panel.Part);
		Assert.Equal(LayoutPart.None, layout.HitTest(0, 0).Part);
	}

	/// <summary>The text an <see cref="AnsiWriter"/> shows, with the escape sequences taken out and columns placed by cursor moves.</summary>
	private static string Visible(AnsiWriter output)
	{
		var raw = Encoding.UTF8.GetString(output.Written);
		var row = new char[200];
		Array.Fill(row, ' ');
		var column = 0;
		for (var i = 0; i < raw.Length; i++)
		{
			if (raw[i] != '\u001b')
			{
				row[column++] = raw[i];
				continue;
			}

			// CSI ... final byte; a cursor move "ESC [ r ; c H" places the next text.
			var end = i + 2;
			while (end < raw.Length && !char.IsAsciiLetter(raw[end]))
			{
				end++;
			}

			if (raw[end] == 'H')
			{
				var parts = raw[(i + 2)..end].Split(';');
				column = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) - 1;
			}

			i = end;
		}

		return new string(row).TrimEnd();
	}
}
