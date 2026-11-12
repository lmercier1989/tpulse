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
