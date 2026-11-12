using System.Text;
using System.Text.RegularExpressions;

using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

public sealed partial class PanelViewTests
{
	[Fact]
	public void WriteLine_ErasesOnlyThePanelCells()
	{
		// Arrange
		var output = new AnsiWriter();

		// Act
		PanelView.WriteLine(output, row: 3, left: 90, width: 34, new PanelLine(PanelLineKind.Field, "host", "WORKSTATION", 1));
		var text = Encoding.UTF8.GetString(output.Written);

		// Assert
		Assert.StartsWith("\u001b[4;91H", text);
		Assert.Contains("\u001b[34X", text);
		Assert.DoesNotContain("\u001b[2K", text);
	}

	[Theory]
	[InlineData(30)]
	[InlineData(34)]
	[InlineData(40)]
	public void WriteLine_Gauge_FillsExactlyThePanelWidth(int width)
	{
		// Arrange
		var output = new AnsiWriter();

		// Act
		PanelView.WriteLine(output, 0, 0, width, new PanelLine(PanelLineKind.Gauge, "C:", string.Empty, 1, 73));
		var visible = Visible(Encoding.UTF8.GetString(output.Written));

		// Assert
		Assert.Equal(width, visible.Length);
		Assert.EndsWith(" 73%", visible);
	}

	[Fact]
	public void WriteLine_FullDisk_TurnsTheGaugeRed()
	{
		// Arrange
		var output = new AnsiWriter();
		var red = PanelView.Warning;

		// Act
		PanelView.WriteLine(output, 0, 0, 34, new PanelLine(PanelLineKind.Gauge, "D:", string.Empty, 1, 95));
		var text = Encoding.UTF8.GetString(output.Written);

		// Assert
		Assert.Contains($"\u001b[38;2;{red.R};{red.G};{red.B}m", text);
	}

	[Fact]
	public void WriteLine_LongValue_IsCutToThePanel()
	{
		// Arrange
		var output = new AnsiWriter();

		// Act
		PanelView.WriteLine(output, 0, 0, 20, new PanelLine(PanelLineKind.Field, "adapter", "Realtek Gaming 2.5GbE Family Controller", 4));
		var visible = Visible(Encoding.UTF8.GetString(output.Written));

		// Assert
		Assert.True(visible.Length <= 20);
		Assert.EndsWith("…", visible);
	}

	[Fact]
	public void HeaderLine_ErasesOnlyTheGraphColumns()
	{
		// Arrange
		var output = new AnsiWriter();
		var readout = new ChannelReadout { Title = "CPU", PrimaryValue = "42%" };

		// Act
		HeaderLine.Write(output, row: 0, left: 2, width: 80, MetricKind.Cpu, readout, compact: false);
		var text = Encoding.UTF8.GetString(output.Written);

		// Assert
		Assert.Contains("\u001b[82X", text);
		Assert.DoesNotContain("\u001b[2K", text);
	}

	private static string Visible(string text) => Escape().Replace(text, string.Empty);

	[GeneratedRegex(@"\u001b\[[0-9;?]*[A-Za-z]")]
	private static partial Regex Escape();
}
