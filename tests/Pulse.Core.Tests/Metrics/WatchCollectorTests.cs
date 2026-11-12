using System.Diagnostics;

using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>
/// The extra graphs and the remote mode against the real counters of the machine the tests run on. The remote mode is
/// pointed at this machine by its own name: PDH reads it locally then, so this covers the remote paths and parsing
/// (NUMA memory, LogicalDisk, Network Interface, Process counters), not the network connection itself.
/// </summary>
public sealed class WatchCollectorTests
{
	private static readonly string Self = Process.GetCurrentProcess().ProcessName;

	[Fact]
	public async Task Collect_ProcessAndCounterWatches_ReadThisProcessAndTheCounter()
	{
		// Arrange
		WatchSpec[] watches = [new(WatchKind.Process, Self), new(WatchKind.Counter, @"\Processor(_Total)\% Processor Time")];
		using var collector = SystemMetricsCollector.Create(null, watches);
		collector.Collect();
		await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

		// Act
		var sample = collector.Collect();

		// Assert
		Assert.Equal(2, sample.Watches.Count);
		var process = sample.Watches[0];
		Assert.True(process.Count >= 1);
		Assert.InRange(process.Value, 0, 100);
		Assert.True(process.MemoryBytes > 1024 * 1024);
		Assert.True(process.Threads >= 1);
		Assert.True(process.Handles >= 1);
		Assert.InRange(sample.Watches[1].Value, 0, 100);
		Assert.Equal(1, sample.Watches[1].Count);
	}

	[Fact]
	public async Task Collect_ThisMachineByName_ReadsTheRemoteCounters()
	{
		// Arrange
		using var collector = SystemMetricsCollector.Create(Environment.MachineName, [new WatchSpec(WatchKind.Process, Self)]);
		collector.Collect();
		await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

		// Act
		var sample = collector.Collect();

		// Assert
		Assert.Equal(Environment.MachineName, collector.Info.Host);
		Assert.False(collector.Info.HasClaude);
		Assert.Null(collector.Info.GpuName);
		Assert.True(sample.MemoryTotalBytes > 0);
		Assert.InRange(sample.MemoryPercent, 0, 100);
		Assert.True(sample.UptimeSeconds > 0);
		Assert.True(sample.NetworkDownBitsPerSecond >= 0 || double.IsNaN(sample.NetworkDownBitsPerSecond));
		Assert.NotNull(sample.Details);
		Assert.True(sample.Details.Remote);
		Assert.Contains(sample.Details.Disks, disk => disk.Name == "C:" && disk.TotalBytes > disk.FreeBytes);
		Assert.True(sample.Watches[0].Count >= 1);
		Assert.True(sample.Watches[0].MemoryBytes > 0);
	}

	[Fact]
	public void Check_ThisMachineAndAnExistingCounter_FindsNothingWrong()
	{
		// Act
		var problem = SystemMetricsCollector.Check(Environment.MachineName, [new WatchSpec(WatchKind.Counter, @"\Memory\Available MBytes")]);

		// Assert
		Assert.Null(problem);
	}

	[Fact]
	public void Check_UnknownCounter_SaysWhichOne()
	{
		// Act
		var problem = SystemMetricsCollector.Check(null, [new WatchSpec(WatchKind.Counter, @"\No Such Object\No Such Counter")]);

		// Assert
		Assert.NotNull(problem);
		Assert.Contains(@"\No Such Object\No Such Counter", problem);
	}

	[Theory]
	[InlineData(0x800007D0u, "Remote Registry")]
	[InlineData(5u, "Performance Monitor Users")]
	[InlineData(0xC0001234u, "0xC0001234")]
	public void DescribeConnectError_SaysWhatToDo(uint status, string expected)
	{
		// Act & Assert
		Assert.Contains(expected, SystemMetricsCollector.DescribeConnectError("sql01", status));
	}
}
