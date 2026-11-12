using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class NiceScaleTests
{
	[Theory]
	[InlineData(0.3, 0.5)]
	[InlineData(1.0, 1.0)]
	[InlineData(1.01, 2.0)]
	[InlineData(2.0, 2.0)]
	[InlineData(2.1, 5.0)]
	[InlineData(5.0, 5.0)]
	[InlineData(7.0, 10.0)]
	[InlineData(999.0, 1000.0)]
	[InlineData(1000.0, 1000.0)]
	[InlineData(1234.0, 2000.0)]
	[InlineData(4_200_000.0, 5_000_000.0)]
	[InlineData(10_350_000.0, 20_000_000.0)]
	public void Ceiling_RoundsUpToNextOneTwoFiveStep(double value, double expected)
	{
		// Act
		var scale = NiceScale.Ceiling(value);

		// Assert
		Assert.Equal(expected, scale, expected * 1e-12);
	}

	[Theory]
	[InlineData(0.0)]
	[InlineData(-5.0)]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	public void Ceiling_ValueWithoutScale_ReturnsOne(double value)
	{
		// Act
		var scale = NiceScale.Ceiling(value);

		// Assert
		Assert.Equal(1.0, scale);
	}
}
