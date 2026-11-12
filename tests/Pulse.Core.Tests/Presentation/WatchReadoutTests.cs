using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Presentation;

/// <summary>The header text of the extra graphs, and of the built-in graphs when another machine is read.</summary>
public sealed class WatchReadoutTests
{
	private static readonly SystemInfo Info = new() { CpuName = "Test CPU", LogicalProcessors = 8, HasCpu = true, HasMemory = true, HasClaude = true };

	[Fact]
	public void Describe_Process_ShowsCpuMemoryAndCounts()
	{
		// Arrange
		var channel = Channel.ForWatch(new WatchSpec(WatchKind.Process, "devenv.exe"), 0);
		var sample = new MetricSnapshot { Time = 1, Watches = [new WatchReading(12.4, 1.5 * 1024 * 1024 * 1024, 9.6, 2, 84, 1204)] };

		// Act
		var readout = ChannelText.Describe(channel, Info, sample, showStats: false);

		// Assert
		Assert.Equal("devenv", readout.Title);
		Assert.Equal(("cpu", "mem"), (readout.PrimaryLabel, readout.SecondaryLabel));
		Assert.Equal(("12%", "10%"), (readout.PrimaryValue, readout.SecondaryValue));
		Assert.StartsWith("2 processes · ", readout.Detail, StringComparison.Ordinal);
		Assert.Contains("84 threads", readout.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public void Describe_ProcessNotRunning_SaysSo()
	{
		// Arrange
		var channel = Channel.ForWatch(new WatchSpec(WatchKind.Process, "devenv"), 0);
		var sample = new MetricSnapshot { Time = 1, Watches = [WatchReading.None] };

		// Act
		var readout = ChannelText.Describe(channel, Info, sample, showStats: false);

		// Assert
		Assert.Equal("not running", readout.Detail);
	}

	[Fact]
	public void Describe_WildcardCounterOnAnotherHost_NamesItsSourceInstancesAndMachine()
	{
		// Arrange
		var channel = Channel.ForWatch(new WatchSpec(WatchKind.Counter, @"\Web Service(*)\Current Connections"), 0);
		var sample = new MetricSnapshot { Time = 1, Watches = [WatchReading.OfCounter(42, 3)] };
		var remote = new SystemInfo { CpuName = "CPU", LogicalProcessors = 0, Host = "web01" };

		// Act
		var readout = ChannelText.Describe(channel, remote, sample, showStats: false);

		// Assert
		Assert.Equal("Current Connections", readout.Title);
		Assert.Equal("42", readout.PrimaryValue);
		Assert.Equal("Web Service(*) · 3 instances added up · on web01", readout.Detail);
		Assert.False(readout.HasSecondary);
	}

	[Theory]
	[InlineData(@"\Processor(_Total)\% Processor Time", 37.26, "37.3%")]
	[InlineData(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", 2_500_000, "2.50 MB/s")]
	[InlineData(@"\Web Service(_Total)\Get Requests/sec", 12.5, "12.5/s")]
	public void Format_FollowsTheCounterUnit(string path, double value, string expected)
	{
		// Arrange
		var channel = Channel.ForWatch(new WatchSpec(WatchKind.Counter, path), 0);

		// Act & Assert
		Assert.Equal(expected, ChannelText.Format(channel, value, System.Globalization.CultureInfo.InvariantCulture));
	}

	[Theory]
	[InlineData(MetricKind.Gpu)]
	[InlineData(MetricKind.Claude)]
	public void Describe_OnAnotherHost_GpuAndClaudeAreLocalOnly(MetricKind kind)
	{
		// Arrange
		var remote = new SystemInfo { CpuName = "CPU", LogicalProcessors = 0, Host = "sql01" };

		// Act
		var readout = ChannelText.Describe(Channel.Create(kind), remote, sample: null, showStats: false);

		// Assert
		Assert.Contains("sql01", readout.Unavailable, StringComparison.Ordinal);
	}

	[Fact]
	public void Describe_CpuWithoutClaude_HasNoClaudeValue()
	{
		// Arrange
		var info = new SystemInfo { CpuName = "CPU", LogicalProcessors = 0, HasCpu = true, Host = "sql01" };
		var sample = new MetricSnapshot { Time = 1, CpuPercent = 40, CpuCorePercents = [40, 40, 40, 40] };

		// Act
		var readout = ChannelText.Describe(Channel.Create(MetricKind.Cpu), info, sample, showStats: false);

		// Assert
		Assert.False(readout.HasSecondary);
		Assert.Contains("4 threads", readout.Detail, StringComparison.Ordinal);
	}
}
