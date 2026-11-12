using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class AutoScaleCeilingTests
{
	[Theory]
	[InlineData(2, 5)]
	[InlineData(30, 50)]
	[InlineData(95, 100)]
	[InlineData(100, 100)]
	public void Observe_WithinTheCeiling_ZoomsInButStopsAtIt(double peak, double expected)
	{
		// Arrange
		var scale = AutoScale.Automatic(5, 100);

		// Act
		scale.Observe(peak);

		// Assert
		Assert.Equal(expected, scale.Target);
	}

	[Fact]
	public void Observe_AboveTheCeiling_StillGrows()
	{
		// Arrange
		var scale = AutoScale.Automatic(5, 100);

		// Act
		scale.Observe(150);

		// Assert
		Assert.Equal(200, scale.Target);
	}
}
