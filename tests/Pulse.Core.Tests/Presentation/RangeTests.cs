using Pulse.Core.Collections;
using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Presentation;

/// <summary>A selected range of time: its statistics, its comparison with a baseline, the stacked CPU and a measured command.</summary>
public sealed class RangeTests
{
	[Fact]
	public void Stats_MinAverageP95Max()
	{
		// Arrange
		var series = new Series(64);
		for (var i = 1; i <= 20; i++)
		{
			series.Add(i, i * 5);
		}

		series.Add(21, double.NaN);

		// Act
		var all = RangeStats.Of(series, 1, 21);
		var part = RangeStats.Of(series, 5, 6);

		// Assert
		Assert.Equal((5, 52.5, 95, 100, 20), (all.Min, all.Average, all.P95, all.Max, all.Count));
		Assert.Equal((25, 30), (part.Min, part.Max));
		Assert.True(RangeStats.Of(series, 30, 40).IsEmpty);
	}

	[Fact]
	public void Compare_PercentagesInPoints_RatesAsARatio()
	{
		// Arrange
		var baseline = Summary(("CPU", 40, true), ("Disk", 100, false));
		var now = Summary(("CPU", 63, true), ("Disk", 150, false), ("GPU", 10, true));

		// Act
		var rows = RangePanel.Compare(baseline, now).ToList();

		// Assert
		Assert.Equal(["CPU", "Disk"], rows.Select(row => row.Now.Title));
		Assert.Equal(("+23 pt", true), (rows[0].Difference, rows[0].Worse));
		Assert.Equal(("+50%", true), (rows[1].Difference, rows[1].Worse));
	}

	[Fact]
	public void Selection_AndReport_ListTheGraphsProcessesAndBaseline()
	{
		// Arrange
		var range = Summary(("CPU", 63, true)) with { Cpu = [new ProcessLoad("dotnet", 7, 41)] };
		var baseline = Summary(("CPU", 40, true));

		// Act
		var lines = RangePanel.Selection(range);
		var report = RangePanel.Report(range, baseline, host: null);

		// Assert
		Assert.Equal(PanelLineKind.Section, lines[0].Kind);
		Assert.Equal("SELECTION", lines[0].Label);
		Assert.EndsWith(" · 1 min 5 s", lines[0].Text, StringComparison.Ordinal);
		Assert.Equal((PanelLineKind.Note, "average", "peak"), (lines[1].Kind, lines[1].Text, lines[1].Right));
		Assert.Contains(lines, line => line is { Label: "CPU", Text: "63", Right: "126" });
		Assert.Contains(lines, line => line is { Label: "dotnet", Text: "×7" });
		Assert.Contains("Busiest CPU   dotnet ×7", report, StringComparison.Ordinal);
		Assert.Contains("Against the baseline", report, StringComparison.Ordinal);
		Assert.Contains("+23 pt", report, StringComparison.Ordinal);
	}

	[Fact]
	public void Stack_InterpolatesBandsBetweenSamples_TheRestUpToTheTotal()
	{
		// Arrange
		var history = new RingBuffer<MetricSnapshot>(8);
		history.Add(new MetricSnapshot { Time = 10, CpuPercent = 50, TopCpu = [new("dotnet", 1, 20), new("node", 1, 10)] });
		history.Add(new MetricSnapshot { Time = 11, CpuPercent = 70, TopCpu = [new("dotnet", 1, 40)] });
		var stack = CpuStack.Of([history[0], history[1]]);
		Span<double> tops = stackalloc double[CpuStack.Named + 1];

		// Act
		var found = stack.TryTops(history, 10.5, 3, tops);

		// Assert
		Assert.True(found);
		Assert.Equal(["dotnet", "node"], stack.Names.Select(name => name.Name));
		Assert.Equal(30, tops[0], 6); // dotnet: halfway from 20 to 40
		Assert.Equal(35, tops[1], 6); // node: 5 on top, halfway from 10 to 0
		Assert.Equal(35, tops[2], 6); // no third process
		Assert.Equal(60, tops[CpuStack.Named], 6); // everything else up to the total
		Assert.False(stack.TryTops(history, 5, 3, tops));
		Assert.False(stack.TryTops(history, 20, 3, tops));
	}

	[Fact]
	public void RunSummary_AddsUpTrafficAndNamesTheBusiest()
	{
		// Arrange
		var summary = new RunSummary();
		for (var i = 0; i < 3; i++)
		{
			summary.Add(new MetricSnapshot
			{
				Time = i,
				CpuPercent = 50,
				MemoryUsedBytes = 8e9,
				MemoryPercent = 50,
				DiskReadBytesPerSecond = 100e6,
				DiskWriteBytesPerSecond = 0,
				NetworkDownBitsPerSecond = 8e6,
				NetworkUpBitsPerSecond = 0,
				TopCpu = [new ProcessLoad("dotnet", 7, 40)],
			});
		}

		// Act
		var lines = summary.Lines("dotnet build", 2.5, 0);

		// Assert
		Assert.StartsWith("tpulse: dotnet build took ", lines[0], StringComparison.Ordinal);
		Assert.EndsWith("exit code 0", lines[0], StringComparison.Ordinal);
		Assert.Contains(lines, line => line.StartsWith("  Disk", StringComparison.Ordinal) && line.Contains("MB", StringComparison.Ordinal));
		Assert.Contains(lines, line => line.StartsWith("  Busiest   dotnet ×7", StringComparison.Ordinal));
		Assert.Single(new RunSummary().Lines("x", 0.1, 1));
	}

	private static RangeSummary Summary(params (string Title, double Average, bool Percent)[] graphs)
	{
		var at = new DateTimeOffset(2026, 11, 12, 14, 32, 5, TimeSpan.Zero);
		return new RangeSummary(
			at,
			at.AddSeconds(65),
			[.. graphs.Select(graph => new RangeGraph(graph.Title, new RangeStats(0, graph.Average, graph.Average, graph.Average * 2, 10), value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture), graph.Percent))],
			[],
			[],
			[]);
	}
}
