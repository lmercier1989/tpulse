using System.Text;
using System.Text.RegularExpressions;

using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

public sealed partial class HeaderLineTests
{
	private static readonly ChannelReadout Disk = new()
	{
		Title = "Disk",
		Detail = "busiest disk 4% active",
		Stats = "peak read 116 MB/s · write 10.9 MB/s",
		Scale = "200 MB/s",
		PrimaryLabel = "read",
		PrimaryValue = "22.6 MB/s",
		SecondaryLabel = "write",
		SecondaryValue = "533 KB/s",
		HasSecondary = true,
	};

	[Theory]
	[InlineData("hello", 10, "hello")]
	[InlineData("hello world", 5, "hell…")]
	[InlineData("hello", 0, "")]
	public void Fit_CutsWithAnEllipsis(string text, int room, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, HeaderLine.Fit(text, room));
	}

	[Fact]
	public void Write_WideRow_ShowsEverything()
	{
		// Act
		var text = Visible(Render(width: 160));

		// Assert
		Assert.Contains("DISK", text);
		Assert.Contains("busiest disk", text);
		Assert.Contains("scale 200 MB/s", text);
		Assert.Contains("write 533 KB/s", text);
	}

	[Fact]
	public void Write_NarrowRow_DropsStatisticsBeforeValues()
	{
		// Act
		var text = Visible(Render(width: 60));

		// Assert
		Assert.DoesNotContain("peak", text);
		Assert.Contains("read 22.6 MB/s", text);
	}

	[Fact]
	public void Write_VeryNarrowRow_KeepsThePrimaryValue()
	{
		// Act
		var text = Visible(Render(width: 30));

		// Assert
		Assert.Contains("22.6 MB/s", text);
		Assert.DoesNotContain("533 KB/s", text);
	}

	[Fact]
	public void FooterLine_HintsLeftStateRight_AtTheWidth()
	{
		// Act
		var wide = HeaderLine.FooterLine(60, "Space pause · Q quit", "1 min · every 1 s");
		var narrow = HeaderLine.FooterLine(30, "Space pause · ← → scroll · Q quit", "1 min · every 1 s");

		// Assert
		Assert.Equal(60, wide.Length);
		Assert.StartsWith("Space pause · Q quit ", wide, StringComparison.Ordinal);
		Assert.EndsWith(" 1 min · every 1 s", wide, StringComparison.Ordinal);
		Assert.Equal(30, narrow.Length);
		Assert.EndsWith("1 min · every 1 s", narrow, StringComparison.Ordinal);
	}

	[Fact]
	public void WriteFooter_PartUnderTheMouse_IsWrittenAgainLit()
	{
		// Arrange
		var output = new AnsiWriter();

		// Act
		HeaderLine.WriteFooter(output, row: 0, left: 0, 60, "Space pause · Q quit", "1 min", highlightStatus: false, hover: (0, 11), hoverBackground: ScopeColors.White);

		// Assert
		var text = Encoding.UTF8.GetString(output.Written);
		Assert.Equal(2, Regex.Count(text, "Space pause"));
		Assert.Contains("\u001b[48;2;255;255;255m", text, StringComparison.Ordinal);
	}

	private static string Render(int width)
	{
		var output = new AnsiWriter();
		HeaderLine.Write(output, row: 0, left: 0, width, MetricKind.Disk, Disk, compact: false);
		return Encoding.UTF8.GetString(output.Written);
	}

	private static string Visible(string text) => Escape().Replace(text, string.Empty);

	[GeneratedRegex(@"\u001b\[[0-9;?]*[A-Za-z]")]
	private static partial Regex Escape();
}
