using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class GpuEngineAggregatorTests
{
	[Fact]
	public void Busiest_SumsProcessesPerEngineAndTakesBusiestEngine()
	{
		// Arrange
		var aggregator = new GpuEngineAggregator();

		// Act: two processes on the 3D engine, one on the copy engine.
		aggregator.Add(physicalAdapter: 0, engine: 0, utilization: 30);
		aggregator.Add(physicalAdapter: 0, engine: 0, utilization: 25);
		aggregator.Add(physicalAdapter: 0, engine: 1, utilization: 40);

		// Assert
		Assert.Equal(55, aggregator.Busiest);
		Assert.Equal(2, aggregator.EngineCount);
	}

	[Fact]
	public void Busiest_SameEngineIndexOnLinkedAdapters_IsNotSummed()
	{
		// Arrange
		var aggregator = new GpuEngineAggregator();

		// Act
		aggregator.Add(0, 0, 30);
		aggregator.Add(1, 0, 30);

		// Assert
		Assert.Equal(30, aggregator.Busiest);
	}

	[Fact]
	public void Busiest_OverOneHundred_IsClamped()
	{
		// Arrange
		var aggregator = new GpuEngineAggregator();

		// Act
		aggregator.Add(0, 0, 70);
		aggregator.Add(0, 0, 70);

		// Assert
		Assert.Equal(100, aggregator.Busiest);
	}

	[Fact]
	public void Add_InvalidUtilization_CountsEngineButAddsNothing()
	{
		// Arrange
		var aggregator = new GpuEngineAggregator();

		// Act
		aggregator.Add(0, 0, double.NaN);
		aggregator.Add(0, 1, -5);

		// Assert
		Assert.Equal(2, aggregator.EngineCount);
		Assert.Equal(0, aggregator.Busiest);
	}

	[Fact]
	public void Reset_ForgetsPreviousSample()
	{
		// Arrange
		var aggregator = new GpuEngineAggregator();
		aggregator.Add(0, 0, 90);

		// Act
		aggregator.Reset();

		// Assert
		Assert.Equal(0, aggregator.EngineCount);
		Assert.Equal(0, aggregator.Busiest);
	}
}
