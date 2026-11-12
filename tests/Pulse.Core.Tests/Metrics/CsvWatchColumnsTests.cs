using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class CsvWatchColumnsTests
{
	[Fact]
	public void WatchColumns_NameEveryExtraGraph_AndNumberRepeats()
	{
		// Arrange
		WatchSpec[] watches =
		[
			new(WatchKind.Process, "Shop.Api"),
			new(WatchKind.Counter, @"\Web Service(_Total)\Current Connections"),
			new(WatchKind.Counter, @"\Web Service(Default Web Site)\Current Connections"),
		];

		// Act
		var columns = CsvRecorder.WatchColumns(watches).ToArray();

		// Assert
		Assert.Equal(["shop_api_cpu_pct", "shop_api_memory_mb", "current_connections", "current_connections_2"], columns);
	}

	[Fact]
	public void Write_WithWatches_AddsTheirValuesAtTheEnd()
	{
		// Arrange
		var path = Path.Combine(Path.GetTempPath(), $"pulse-watch-{Guid.NewGuid():N}.csv");
		WatchSpec[] watches = [new(WatchKind.Process, "devenv"), new(WatchKind.Counter, @"\Memory\Available MBytes")];
		try
		{
			// Act
			using (var recorder = new CsvRecorder(path, watches))
			{
				recorder.Write(new MetricSnapshot { Time = 1, Watches = [new WatchReading(12.5, 2 * 1024 * 1024, 1, 1, 10, 10), WatchReading.OfCounter(8123, 1)] });
			}

			// Assert
			var lines = File.ReadAllLines(path);
			Assert.EndsWith(",devenv_cpu_pct,devenv_memory_mb,available_mbytes", lines[0], StringComparison.Ordinal);
			Assert.EndsWith(",12.5,2,8123", lines[1], StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void DefaultPath_WithHost_NamesTheMachine()
	{
		// Act & Assert
		Assert.StartsWith("pulse-sql01-", Path.GetFileName(CsvRecorder.DefaultPath("SQL01")), StringComparison.Ordinal);
	}
}
