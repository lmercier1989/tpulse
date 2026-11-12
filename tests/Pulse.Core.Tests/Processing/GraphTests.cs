using Pulse.Core.Metrics;
using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.Tests.Processing;

public sealed class GraphTests
{
	private static SystemInfo Info(IReadOnlyList<WatchSpec> watches, params string[] repositories)
		=> new() { CpuName = "CPU", LogicalProcessors = 8, Watches = watches, GitRepositories = repositories };

	[Fact]
	public void CreateAll_BuiltInGraphsFirst_ThenGit_ThenOnePerWatch()
	{
		// Act
		var graphs = Graph.CreateAll(new PulseSettings(), new PulseSettings(), Info([new WatchSpec(WatchKind.Process, "devenv")], "Pulse"));

		// Assert
		Assert.Equal([.. MetricKinds.Builtin, MetricKind.Process], graphs.Select(graph => graph.Kind));
		Assert.Equal(0, graphs[^1].Channel.WatchIndex);
		Assert.True(graphs[^1].Visible);
	}

	[Fact]
	public void CreateAll_GitIsTheSeventhBuiltInGraph_OnKey7()
	{
		// Act
		var graphs = Graph.CreateAll(new PulseSettings(), new PulseSettings(), Info([new WatchSpec(WatchKind.Process, "devenv")]));

		// Assert
		Assert.Equal(MetricKind.Git, graphs[6].Kind);
		Assert.Equal('7', Graph.KeyOf(6));
		Assert.Equal('8', Graph.KeyOf(7));
		Assert.Equal(MetricKinds.Builtin.Count, new PulseSettings().Channels.Length);
	}

	[Fact]
	public void SetVisible_BuiltInGraph_IsRemembered_ExtraGraphIsNot()
	{
		// Arrange
		var session = new PulseSettings();
		var saved = new PulseSettings();
		var graphs = Graph.CreateAll(session, saved, Info([new WatchSpec(WatchKind.Process, "devenv")]));

		// Act
		graphs[(int) MetricKind.Disk].SetVisible(false);
		graphs[^1].SetSize(ChannelSize.Tall);

		// Assert
		Assert.False(session.Channel(MetricKind.Disk).Visible);
		Assert.False(saved.Channel(MetricKind.Disk).Visible);
		Assert.Equal(ChannelSize.Tall, graphs[^1].Size);
		Assert.All(saved.Channels, channel => Assert.Equal(ChannelSize.Normal, channel.Size));
	}

	[Theory]
	[InlineData(1, 6, "1 to 6")]
	[InlineData(7, 1, "7")]
	[InlineData(7, 3, "7 to 9")]
	[InlineData(7, 4, "7 to 9, 0")]
	[InlineData(10, 1, "0")]
	public void KeyRange_ReadsNaturally(int first, int count, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, Graph.KeyRange(first, count));
	}

	[Theory]
	[InlineData(0, '1')]
	[InlineData(8, '9')]
	[InlineData(9, '0')]
	public void KeyOf_TheTenthGraphIsZero(int index, char expected)
	{
		// Act & Assert
		Assert.Equal(expected, Graph.KeyOf(index));
	}
}
