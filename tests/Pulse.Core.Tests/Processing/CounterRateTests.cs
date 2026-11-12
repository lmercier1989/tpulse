using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class CounterRateTests
{
	[Fact]
	public void Complete_FirstSample_ReturnsNaN()
	{
		// Arrange
		var rate = new CounterRate();
		rate.Begin();
		rate.Add(1, 1000, 500);

		// Act
		var (inRate, outRate) = rate.Complete(1);

		// Assert
		Assert.True(double.IsNaN(inRate));
		Assert.True(double.IsNaN(outRate));
	}

	[Fact]
	public void Complete_SecondSample_ReturnsDeltaPerSecondSummedOverInterfaces()
	{
		// Arrange
		var rate = new CounterRate();
		Sample(rate, 1, (1, 1000, 500), (2, 10, 10));

		// Act
		var (inRate, outRate) = Sample(rate, 2, (1, 3000, 1500), (2, 210, 50));

		// Assert
		Assert.Equal((2000 + 200) / 2.0, inRate);
		Assert.Equal((1000 + 40) / 2.0, outRate);
	}

	[Fact]
	public void Complete_InterfaceThatJustAppeared_IsNotCounted()
	{
		// Arrange
		var rate = new CounterRate();
		Sample(rate, 1, (1, 1000, 1000));

		// Act
		var (inRate, outRate) = Sample(rate, 1, (1, 1100, 1200), (7, 999_999, 999_999));

		// Assert
		Assert.Equal(100, inRate);
		Assert.Equal(200, outRate);
	}

	[Fact]
	public void Complete_CounterThatWentBackwards_IsSkipped()
	{
		// Arrange
		var rate = new CounterRate();
		Sample(rate, 1, (1, 5000, 5000), (2, 100, 100));

		// Act
		var (inRate, outRate) = Sample(rate, 1, (1, 10, 10), (2, 150, 300));

		// Assert
		Assert.Equal(50, inRate);
		Assert.Equal(200, outRate);
	}

	[Fact]
	public void Complete_NoElapsedTime_ReturnsNaN()
	{
		// Arrange
		var rate = new CounterRate();
		Sample(rate, 1, (1, 0, 0));

		// Act
		var (inRate, _) = Sample(rate, 0, (1, 100, 100));

		// Assert
		Assert.True(double.IsNaN(inRate));
	}

	private static (double In, double Out) Sample(CounterRate rate, double elapsed, params (ulong Key, ulong In, ulong Out)[] interfaces)
	{
		rate.Begin();
		foreach (var (key, inTotal, outTotal) in interfaces)
		{
			rate.Add(key, inTotal, outTotal);
		}

		return rate.Complete(elapsed);
	}
}
