using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>Reads the real counters of the machine the tests run on; only checks what every Windows and Linux system has.</summary>
public sealed class SystemMetricsCollectorTests
{
	[Fact]
	public async Task Collect_TwoSamples_ReportsPlausibleCoreMetrics()
	{
		// Arrange
		using var collector = SystemMetricsCollector.Create();
		collector.Collect();
		await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

		// Act
		var sample = collector.Collect();

		// Assert
		Assert.InRange(sample.MemoryPercent, 0, 100);
		Assert.True(sample.MemoryTotalBytes > 0);
		Assert.Equal(sample.MemoryTotalBytes - sample.MemoryAvailableBytes, sample.MemoryUsedBytes);
		Assert.True(sample.NetworkDownBitsPerSecond >= 0 || double.IsNaN(sample.NetworkDownBitsPerSecond));
		if (collector.Info.HasCpu)
		{
			Assert.InRange(sample.CpuPercent, 0, 100);
		}

		Assert.All(sample.CpuCorePercents, core => Assert.True(double.IsNaN(core) || core is >= 0 and <= 100));
		Assert.False(string.IsNullOrWhiteSpace(collector.Info.CpuName));
	}

	[Fact]
	public void Collect_AfterDispose_Throws()
	{
		// Arrange
		var collector = SystemMetricsCollector.Create();
		collector.Dispose();

		// Act
		var exception = Record.Exception(() => collector.Collect());

		// Assert
		Assert.IsType<ObjectDisposedException>(exception);
	}
}
