using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class ProcessorInstanceNameTests
{
	[Theory]
	[InlineData("0,0", 0)]
	[InlineData("0,13", 13)]
	[InlineData("1,2", 1026)]
	[InlineData("7", 7)]
	public void TryParse_ProcessorInstance_ReturnsSortableKey(string name, int expected)
	{
		// Act
		var parsed = ProcessorInstanceName.TryParse(name, out var key);

		// Assert
		Assert.True(parsed);
		Assert.Equal(expected, key);
	}

	[Theory]
	[InlineData("_Total")]
	[InlineData("0,_Total")]
	[InlineData("")]
	[InlineData("a,b")]
	public void TryParse_TotalOrMalformed_ReturnsFalse(string name)
	{
		// Act
		var parsed = ProcessorInstanceName.TryParse(name, out _);

		// Assert
		Assert.False(parsed);
	}

	[Fact]
	public void TryParse_Keys_SortInProcessorOrder()
	{
		// Arrange
		string[] names = ["1,0", "0,10", "0,2"];

		// Act
		var keys = names.Select(name => ProcessorInstanceName.TryParse(name, out var key) ? key : -1).Order().ToArray();

		// Assert
		Assert.Equal(new[] { 2, 10, 1024 }, keys);
	}
}
