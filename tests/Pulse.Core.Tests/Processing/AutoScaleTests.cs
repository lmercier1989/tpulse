using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class AutoScaleTests
{
	private const double Floor = 1_000_000;

	[Fact]
	public void Fixed_ObservingAnyPeak_NeverMoves()
	{
		// Arrange
		var scale = AutoScale.Fixed(100);

		// Act
		scale.Observe(500);
		var animating = scale.Advance(0.1);

		// Assert
		Assert.Equal(100, scale.Target);
		Assert.Equal(100, scale.Current);
		Assert.False(animating);
	}

	[Fact]
	public void Automatic_Initially_SitsOnItsFloor()
	{
		// Act
		var scale = AutoScale.Automatic(Floor);

		// Assert
		Assert.Equal(Floor, scale.Target);
		Assert.Equal(Floor, scale.Current);
	}

	[Fact]
	public void Observe_PeakAboveScale_GrowsToNextStepWithHeadroom()
	{
		// Arrange
		var scale = AutoScale.Automatic(Floor);

		// Act
		scale.Observe(9_000_000); // 9 MB/s plus 15 % headroom needs 10.35 MB/s.

		// Assert
		Assert.Equal(20_000_000, scale.Target);
	}

	[Fact]
	public void Observe_PeakSlightlyLower_KeepsScale()
	{
		// Arrange
		var scale = AutoScale.Automatic(Floor);
		scale.Observe(9_000_000);

		// Act
		scale.Observe(8_000_000);

		// Assert
		Assert.Equal(20_000_000, scale.Target);
	}

	[Fact]
	public void Observe_PeakFarLower_ShrinksToSmallerStep()
	{
		// Arrange
		var scale = AutoScale.Automatic(Floor);
		scale.Observe(40_000_000);

		// Act
		scale.Observe(4_000_000);

		// Assert
		Assert.Equal(10_000_000, scale.Target);
	}

	[Theory]
	[InlineData(0.0)]
	[InlineData(double.NaN)]
	public void Observe_NoTraffic_NeverDropsBelowFloor(double peak)
	{
		// Arrange
		var scale = AutoScale.Automatic(Floor);
		scale.Observe(40_000_000);

		// Act
		scale.Observe(peak);

		// Assert
		Assert.Equal(Floor, scale.Target);
	}

	[Fact]
	public void Advance_AfterGrowing_EasesUpwardThenSnapsToTarget()
	{
		// Arrange
		var scale = AutoScale.Automatic(Floor);
		scale.Observe(9_000_000);
		var previous = scale.Current;

		// Act & Assert
		for (var frame = 0; frame < 120 && scale.Current != scale.Target; frame++)
		{
			Assert.True(scale.Advance(1.0 / 60));
			Assert.True(scale.Current > previous);
			Assert.True(scale.Current <= scale.Target);
			previous = scale.Current;
		}

		Assert.Equal(scale.Target, scale.Current);
		Assert.False(scale.Advance(1.0 / 60));
	}
}
