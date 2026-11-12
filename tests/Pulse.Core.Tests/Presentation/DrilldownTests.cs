using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Presentation;

/// <summary>Looking into a moment: what was busiest then, what happened around it, and what carried the load in view.</summary>
public sealed class DrilldownTests
{
	private static readonly DateTimeOffset Now = TimeProvider.System.GetUtcNow();

	[Fact]
	public void AverageCpu_ALastingLoad_WeighsMoreThanASpike()
	{
		// Arrange: dotnet at 20 % all along, a 90 % spike of devenv in one of four samples.
		MetricSnapshot[] samples =
		[
			Sample(new ProcessLoad("dotnet", 7, 20)),
			Sample(new ProcessLoad("dotnet", 7, 20), new ProcessLoad("devenv", 1, 90)),
			Sample(new ProcessLoad("dotnet", 7, 20)),
			Sample(new ProcessLoad("dotnet", 7, 20), new ProcessLoad("msedge", 24, 0.1)),
		];

		// Act
		var top = ProcessCausation.AverageCpu(samples, 5);

		// Assert
		Assert.Equal(["devenv", "dotnet"], top.Select(load => load.Name)); // msedge averages 0.025 %: left out.
		Assert.Equal(22.5, top[0].Value);
		Assert.Equal(20, top[1].Value);
		Assert.Equal(7, top[1].Count);
		Assert.Empty(ProcessCausation.AverageCpu([], 5));
	}

	[Fact]
	public void Build_WithFocus_StartsWithTheMoment_AndLeavesOutTheLiveTopLists()
	{
		// Arrange
		var sample = new MetricSnapshot
		{
			Time = 10,
			TopCpu = [.. Enumerable.Range(1, 8).Select(i => new ProcessLoad($"p{i}", 1, 10 - i))],
			TopIo = [new ProcessLoad("sqlservr", 1, 12_000_000)],
		};
		var focus = new PanelFocus(
			Now,
			Pinned: true,
			[new PulseEvent(9, Now.AddSeconds(-1), EventKind.Git, "Pulse", "commit", "Fix the login")],
			[new ProcessLoad("dotnet", 7, 20)]);

		// Act
		var lines = InfoPanel.Build(null, sample, null, events: null, focus);

		// Assert
		Assert.Equal((PanelLineKind.Section, "MOMENT"), (lines[0].Kind, lines[0].Label));
		Assert.EndsWith(" · frozen", lines[0].Text, StringComparison.Ordinal);
		Assert.Equal(InfoPanel.MaximumFocusTop, lines.Count(line => line.Label.StartsWith('p')));
		Assert.Contains(lines, line => line.Label == "sqlservr" && line.Right.Contains("MB/s", StringComparison.Ordinal));
		Assert.Contains(lines, line => line is { Label: "commit", Text: "Pulse · Fix the login" });
		Assert.Contains(lines, line => line is { Label: "IN VIEW", Text: "average CPU" });
		Assert.Contains(lines, line => line is { Label: "dotnet", Text: "×7" });
		Assert.DoesNotContain(lines, line => line.Label == "TOP CPU");
	}

	[Fact]
	public void Build_Live_ListsTheFirstThreeOnly()
	{
		// Arrange
		var sample = new MetricSnapshot { Time = 10, TopCpu = [.. Enumerable.Range(1, 10).Select(i => new ProcessLoad($"p{i}", 1, 11 - i))] };

		// Act
		var lines = InfoPanel.Build(null, sample, null);

		// Assert
		Assert.Equal(InfoPanel.MaximumTop, lines.Count(line => line.Label.StartsWith('p')));
	}

	[Fact]
	public void Loads_RoundTripThroughTheRecording()
	{
		// Arrange
		ProcessLoad[] loads = [new("dotnet", 7, 70.4), new("node", 15, 5.75)];

		// Act
		var cell = CsvRecorder.Loads(loads);
		var back = CsvRecorder.ParseLoads(cell);

		// Assert
		Assert.Equal("dotnet:70.4:7|node:5.75:15", cell);
		Assert.Equal(loads, back);
		Assert.Empty(CsvRecorder.ParseLoads(null));
	}

	private static MetricSnapshot Sample(params ProcessLoad[] loads) => new() { Time = 1, TopCpu = loads };
}
