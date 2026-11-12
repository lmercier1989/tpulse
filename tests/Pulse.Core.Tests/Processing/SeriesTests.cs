using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class SeriesTests
{
	[Theory]
	[InlineData(-5.0, 0)]
	[InlineData(0.0, 0)]
	[InlineData(0.5, 1)]
	[InlineData(2.0, 2)]
	[InlineData(3.0, 3)]
	[InlineData(9.0, 4)]
	public void LowerBound_ReturnsFirstSampleAtOrAfterTime(double time, int expected)
	{
		// Arrange
		var series = Create((0, 1), (1, 2), (2, 3), (3, 4));

		// Act
		var index = series.LowerBound(time);

		// Assert
		Assert.Equal(expected, index);
	}

	[Fact]
	public void Range_SkipsNaNAndSamplesOutsideWindow()
	{
		// Arrange
		var series = Create((0, 100), (1, 5), (2, double.NaN), (3, 40), (4, 12), (5, 900));

		// Act
		var range = series.Range(1, 4);

		// Assert
		Assert.Equal(new ValueRange(5, 40), range);
	}

	[Fact]
	public void Range_WithoutFiniteValues_IsEmpty()
	{
		// Arrange
		var series = Create((0, double.NaN), (1, double.NaN));

		// Act
		var range = series.Range(0, 1);

		// Assert
		Assert.True(range.IsEmpty);
	}

	[Fact]
	public void Latest_ReturnsNewestValueOrNaN()
	{
		// Arrange
		var empty = new Series(4);
		var filled = Create((0, 1), (1, 7));

		// Act & Assert
		Assert.True(double.IsNaN(empty.Latest));
		Assert.Equal(7, filled.Latest);
	}

	private static Series Create(params (double Time, double Value)[] samples)
	{
		var series = new Series(16);
		foreach (var (time, value) in samples)
		{
			series.Add(time, value);
		}

		return series;
	}
}
