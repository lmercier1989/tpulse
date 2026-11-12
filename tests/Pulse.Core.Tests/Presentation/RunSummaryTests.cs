using Pulse.Core.Metrics;
using Pulse.Core.Presentation;

namespace Pulse.Core.Tests.Presentation;

/// <summary>What a measured command adds to its summary: the run as the history keeps it, and what it left running.</summary>
public sealed class RunSummaryTests
{
	[Fact]
	public void Record_AverageCpuAndPeakMemory_LeaveOutTheFirstSample()
	{
		// Arrange
		var summary = new RunSummary();
		summary.Add(new MetricSnapshot { Time = 0, CpuPercent = 100, MemoryUsedBytes = 9e9 });
		summary.Add(new MetricSnapshot { Time = 1, CpuPercent = 40, MemoryUsedBytes = 2e9 });
		summary.Add(new MetricSnapshot { Time = 2, CpuPercent = 60, MemoryUsedBytes = 3e9 });

		// Act
		var record = summary.Record(TimeProvider.System.GetUtcNow(), "/src/shop", "make", 2.1, 0, "abc");

		// Assert
		Assert.Equal(50, record.CpuAverage, 6);
		Assert.Equal(3e9, record.MemoryPeakBytes);
		Assert.Equal(("make", 2.1, 0, "abc"), (record.Command, record.Seconds, record.ExitCode, record.Commit));
	}

	[Fact]
	public void Record_TooFewSamples_IsNotMeasured()
	{
		// Act
		var record = new RunSummary().Record(TimeProvider.System.GetUtcNow(), "/src", "x", 0.1, 1, null);

		// Assert
		Assert.True(double.IsNaN(record.CpuAverage));
		Assert.True(double.IsNaN(record.MemoryPeakBytes));
	}

	[Fact]
	public void LeftRunning_Nothing_IsNull()
		=> Assert.Null(RunSummary.LeftRunning([]));

	[Fact]
	public void LeftRunning_ByNameMostMemoryFirst_WithTheIds()
	{
		// Arrange
		LeftProcess[] left = [new("MSBuild", 4120, 200_000_000), new("VBCSCompiler", 5532, 300_000_000), new("MSBuild", 4188, 210_000_000)];

		// Act
		var line = RunSummary.LeftRunning(left);

		// Assert
		Assert.NotNull(line);
		Assert.StartsWith("  Left on   MSBuild ×2 ", line, StringComparison.Ordinal);
		Assert.Contains(", VBCSCompiler ", line, StringComparison.Ordinal);
		Assert.EndsWith(" · ids 4120 5532 4188", line, StringComparison.Ordinal);
	}
}
