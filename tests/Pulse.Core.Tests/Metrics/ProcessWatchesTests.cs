using Pulse.Core.Interop;
using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class ProcessWatchesTests
{
	private static readonly WatchSpec[] Watches =
	[
		new(WatchKind.Process, "devenv"),
		new(WatchKind.Counter, @"\Memory\Available MBytes"),
		new(WatchKind.Process, "300"),
	];

	[Fact]
	public void Sample_AddsUpEveryProcessOfAName_AndFindsOneById()
	{
		// Arrange
		var watches = new ProcessWatches(Watches);
		ProcessRecord[] records =
		[
			Record(100, watch: 0, bytes: 400, threads: 30, handles: 900),
			Record(101, watch: 0, bytes: 100, threads: 10, handles: 100),
			Record(200, watch: -1, bytes: 999, threads: 99, handles: 999),
			Record(300, watch: -1, bytes: 50, threads: 5, handles: 60),
		];
		var readings = new WatchReading[Watches.Length];

		// Act
		watches.Sample(records, memoryTotalBytes: 1000, readings);

		// Assert
		Assert.Equal(2, readings[0].Count);
		Assert.Equal(500, readings[0].MemoryBytes);
		Assert.Equal(50, readings[0].MemoryPercent);
		Assert.Equal(40, readings[0].Threads);
		Assert.Equal(1000, readings[0].Handles);
		Assert.True(double.IsNaN(readings[0].Value)); // The first snapshot has no interval to measure CPU over.
		Assert.Equal(default, readings[1]); // A counter is left to the collector.
		Assert.Equal(1, readings[2].Count);
		Assert.Equal(50, readings[2].MemoryBytes);
	}

	[Fact]
	public void Sample_NothingRunning_ReportsNone()
	{
		// Arrange
		var watches = new ProcessWatches(Watches);
		var readings = new WatchReading[Watches.Length];

		// Act
		watches.Sample([Record(7, watch: -1, bytes: 1, threads: 1, handles: 1)], 1000, readings);

		// Assert
		Assert.Equal(0, readings[0].Count);
		Assert.True(double.IsNaN(readings[0].MemoryBytes));
	}

	[Fact]
	public void Sample_SecondSnapshot_MeasuresCpu()
	{
		// Arrange
		var watches = new ProcessWatches(Watches);
		var readings = new WatchReading[Watches.Length];
		watches.Sample([Record(100, watch: 0, bytes: 1, threads: 1, handles: 1)], 1000, readings);
		Thread.Sleep(50);

		// Act
		watches.Sample([Record(100, watch: 0, bytes: 1, threads: 1, handles: 1, cpu: 1000)], 1000, readings);

		// Assert
		Assert.InRange(readings[0].Value, 0, 100);
	}

	private static ProcessRecord Record(int id, int watch, long bytes, int threads, int handles, long cpu = 0)
		=> new() { Id = id, ParentId = 4, CreateTime = 1, CpuTime = cpu, PrivateBytes = bytes, Threads = threads, Handles = handles, Watch = watch };
}
