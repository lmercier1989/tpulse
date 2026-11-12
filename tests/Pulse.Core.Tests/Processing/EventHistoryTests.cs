using Pulse.Core.Metrics;
using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class EventHistoryTests
{
	[Fact]
	public void Add_OutOfOrder_KeepsTimeOrder_AndListsTheLatestFirst()
	{
		// Arrange
		var history = new EventHistory();

		// Act
		history.Add([Event(5), Event(1)]);
		history.Add([Event(3)]);

		// Assert
		Assert.Equal([1.0, 3.0, 5.0], history.Between(0, 10).Select(item => item.Time));
		Assert.Equal([5.0, 3.0], history.Latest(2).Select(item => item.Time));
		Assert.Equal([3.0, 1.0], history.Latest(5, time: 4).Select(item => item.Time));
		Assert.Equal(3, history.Version);
	}

	[Fact]
	public void Add_PastTheCapacity_DropsTheOldest()
	{
		// Arrange
		var history = new EventHistory();

		// Act
		history.Add(Enumerable.Range(0, EventHistory.Capacity + 10).Select(i => Event(i)));

		// Assert
		Assert.Equal(EventHistory.Capacity, history.Count);
		Assert.Equal(10, history.Between(0, double.PositiveInfinity).First().Time);
	}

	private static PulseEvent Event(double time) => new(time, TimeProvider.System.GetUtcNow(), EventKind.Git, "repo", "commit", "x");
}
