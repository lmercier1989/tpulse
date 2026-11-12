using System.Text;

using Pulse.Core.Processing;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

public sealed class BrailleScopeTests
{
	[Fact]
	public void DrawLine_FlatTraceOnTheBottomDots_FillsBothBottomDotsOfEachCell()
	{
		// Arrange
		var scope = new BrailleScope();
		scope.Reset(columns: 2, rows: 1);
		var ys = Enumerable.Repeat(3.0, scope.DotWidth + 1).ToArray();
		var output = new AnsiWriter();

		// Act
		scope.DrawLine(ys, BrailleLayer.Primary);
		scope.WriteTo(output, row: 4, column: 2, MetricKind.Cpu);
		var text = Encoding.UTF8.GetString(output.Written);

		// Assert: U+28C0 is dots 7 and 8, the bottom row of a braille cell.
		Assert.StartsWith("\u001b[5;3H", text);
		Assert.Equal(2, text.Count(character => character == '⣀'));
	}

	[Fact]
	public void DrawLine_RisingTrace_ConnectsTheDots()
	{
		// Arrange: from the bottom to the top across one cell.
		var scope = new BrailleScope();
		scope.Reset(columns: 1, rows: 1);
		double[] ys = [3, 0, 0];
		var output = new AnsiWriter();

		// Act
		scope.DrawLine(ys, BrailleLayer.Primary);
		scope.WriteTo(output, 0, 0, MetricKind.Cpu);
		var text = Encoding.UTF8.GetString(output.Written);

		// Assert: the left column fully set (dots 1, 2, 3, 7) and the right top dot (4).
		Assert.Contains((char) (0x2800 | 0x01 | 0x02 | 0x04 | 0x40 | 0x08), text);
	}

	[Fact]
	public void DrawFill_ColoursCellsBelowTheTraceOnly()
	{
		// Arrange
		var scope = new BrailleScope();
		scope.Reset(columns: 1, rows: 3);
		var ys = Enumerable.Repeat(4.5, scope.DotWidth + 1).ToArray();
		var output = new AnsiWriter();

		// Act
		scope.DrawFill(ys);
		scope.WriteTo(output, 0, 0, MetricKind.Memory);
		var text = Encoding.UTF8.GetString(output.Written);

		// Assert: the top row keeps the panel colour, the lower rows get a fill colour.
		var background = ScopeColors.Background;
		Assert.Contains($"\u001b[48;2;{background.R};{background.G};{background.B}m", text);
		Assert.Equal(3, text.Split("\u001b[48;2;").Length - 1);
	}
}
