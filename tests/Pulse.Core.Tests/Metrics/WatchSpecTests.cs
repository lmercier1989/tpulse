using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class WatchSpecTests
{
	[Theory]
	[InlineData("devenv", "devenv", null, "devenv")]
	[InlineData("Shop.Api.exe", "Shop.Api", null, "Shop.Api")]
	[InlineData("4120", "", 4120, "pid 4120")]
	public void Process_NameIdAndTitle(string target, string name, int? id, string title)
	{
		// Arrange
		var watch = new WatchSpec(WatchKind.Process, target);

		// Act & Assert
		Assert.Equal(name, watch.ProcessName);
		Assert.Equal(id, watch.ProcessId);
		Assert.Equal(title, watch.Title);
		Assert.Null(watch.Counter);
	}

	[Fact]
	public void Counter_TitleIsTheCounterName()
	{
		// Arrange
		var watch = new WatchSpec(WatchKind.Counter, @"\Web Service(_Total)\Current Connections");

		// Act & Assert
		Assert.Equal("Current Connections", watch.Title);
		Assert.Equal(string.Empty, watch.ProcessName);
		Assert.Null(watch.ProcessId);
		Assert.NotNull(watch.Counter);
	}

	[Fact]
	public void ImageNames_MarkOnlyProcessesByName()
	{
		// Arrange
		WatchSpec[] watches =
		[
			new(WatchKind.Process, "devenv"),
			new(WatchKind.Counter, @"\Memory\Available MBytes"),
			new(WatchKind.Process, "4120"),
		];

		// Act & Assert
		Assert.Equal(["devenv.exe", "", ""], ProcessWatches.ImageNames(watches));
	}
}
