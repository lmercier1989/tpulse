using Pulse.Core.Interop;
using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.Tests.Presentation;

/// <summary>Your order of the graphs and the panel sections, hiding sections, rows that say what they stand for.</summary>
public sealed class LayoutTests
{
	[Fact]
	public void Sort_NamedFirst_TheRestInTheirOwnOrderAfter()
	{
		// Act
		var sorted = LayoutOrder.Sort(["SYSTEM", "CLAUDE", "GIT", "DISKS"], name => name, ["git", "SYSTEM"]);

		// Assert
		Assert.Equal(["GIT", "SYSTEM", "CLAUDE", "DISKS"], sorted);
	}

	[Fact]
	public void Move_TradesPlacesWithTheNearestShownNeighbour()
	{
		// Arrange
		string[] order = ["Cpu", "Gpu", "Memory", "Disk"];

		// Act
		var past = LayoutOrder.Move(order, name => name != "Gpu", "Memory", -1);
		var first = LayoutOrder.Move(order, _ => true, "Cpu", -1);

		// Assert
		Assert.Equal(["Memory", "Gpu", "Cpu", "Disk"], past); // the hidden GPU keeps its place
		Assert.Equal(order, first);
	}

	[Fact]
	public void DisplayOrder_BuiltinInYourOrder_ExtraGraphsAfter()
	{
		// Arrange
		var info = new SystemInfo { CpuName = "CPU", LogicalProcessors = 8, Watches = [new WatchSpec(WatchKind.Process, "devenv")] };
		var graphs = Graph.CreateAll(new PulseSettings(), new PulseSettings(), info);

		// Act
		var order = Graph.DisplayOrder(graphs, ["Git", "Cpu"]);

		// Assert
		Assert.Equal([MetricKind.Git, MetricKind.Cpu, MetricKind.Gpu], order.Take(3).Select(index => graphs[index].Kind));
		Assert.Equal(MetricKind.Process, graphs[order[^1]].Kind);
		Assert.Equal(string.Empty, graphs[order[^1]].OrderName);
	}

	[Fact]
	public void Arrange_OrdersAndHidesSections_OneBlankRowBetween()
	{
		// Arrange
		PanelLine[] lines =
		[
			new(PanelLineKind.Section, "SYSTEM", string.Empty, 0),
			new(PanelLineKind.Field, "host", "pc", 1),
			new(PanelLineKind.Blank, string.Empty, string.Empty, 9),
			new(PanelLineKind.Section, "NETWORK", string.Empty, 0),
			new(PanelLineKind.Field, "ipv4", "10.0.0.2", 1),
			new(PanelLineKind.Blank, string.Empty, string.Empty, 9),
			new(PanelLineKind.Section, "DISKS", string.Empty, 0),
			new(PanelLineKind.Gauge, "C:", string.Empty, 1, 50),
		];

		// Act
		var arranged = InfoPanel.Arrange(lines, ["DISKS"], ["network"]);

		// Assert
		Assert.Equal(["DISKS", "C:", string.Empty, "SYSTEM", "host"], arranged.Select(line => line.Label));
		Assert.Equal(["DISKS", "NETWORK", "SYSTEM"], InfoPanel.Sections(lines).Select(section => section.Title).Order());
	}

	[Fact]
	public void Build_RowsSayWhatTheyStandFor()
	{
		// Arrange
		var sample = new MetricSnapshot { Time = 10, TopCpu = [new ProcessLoad("dotnet", 7, 40)] };
		var details = new SystemDetails("pc", "Windows", [new DiskSpace("D:", "Dev", 100, 40)], null, null, false);
		var events = new[] { new PulseEvent(9, DateTimeOffset.Now, EventKind.Git, "Webshop", "commit", "fix") };

		// Act
		var lines = InfoPanel.Build(details, sample, null, events);

		// Assert
		Assert.Contains(lines, line => line is { Label: "dotnet", Link: { Kind: PanelLinkKind.Process, Value: "dotnet" } });
		Assert.Contains(lines, line => line is { Kind: PanelLineKind.Gauge, Link: { Kind: PanelLinkKind.Disk, Value: @"D:\" } });
		Assert.Contains(lines, line => line is { Label: "commit", Link: { Kind: PanelLinkKind.Event, Time: 9 } });
		Assert.Contains(lines, line => line is { Kind: PanelLineKind.Section, Label: "TOP CPU", Text: "right now" });
	}

	[Fact]
	public void TryFollowProcess_AddsAReadingFromTheNextSample()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Arrange
		using var collector = SystemMetricsCollector.Create();
		var name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

		// Act
		var added = collector.TryFollowProcess(name, out var watch, out var index);
		var again = collector.TryFollowProcess(name, out _, out _);
		var sample = collector.Collect();

		// Assert
		Assert.True(added);
		Assert.False(again);
		Assert.Equal((WatchKind.Process, name, 0), (watch.Kind, watch.Target, index));
		Assert.True(sample.Watches[index].Count >= 1);
	}
}
