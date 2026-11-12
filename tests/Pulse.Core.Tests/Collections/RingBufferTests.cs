using Pulse.Core.Collections;

namespace Pulse.Core.Tests.Collections;

public sealed class RingBufferTests
{
	[Fact]
	public void Add_BelowCapacity_KeepsInsertionOrder()
	{
		// Arrange
		var buffer = new RingBuffer<int>(4);

		// Act
		buffer.Add(1);
		buffer.Add(2);
		buffer.Add(3);

		// Assert
		Assert.Equal(3, buffer.Count);
		Assert.False(buffer.IsFull);
		Assert.Equal(new[] { 1, 2, 3 }, Items(buffer));
	}

	[Fact]
	public void Add_BeyondCapacity_OverwritesOldestFirst()
	{
		// Arrange
		var buffer = new RingBuffer<int>(3);

		// Act
		for (var i = 1; i <= 5; i++)
		{
			buffer.Add(i);
		}

		// Assert
		Assert.True(buffer.IsFull);
		Assert.Equal(3, buffer.Count);
		Assert.Equal(new[] { 3, 4, 5 }, Items(buffer));
		Assert.Equal(5, buffer.Newest);
	}

	[Fact]
	public void Add_ManyWrapArounds_KeepsCapacityAndOrder()
	{
		// Arrange
		var buffer = new RingBuffer<int>(5);

		// Act
		for (var i = 0; i < 1003; i++)
		{
			buffer.Add(i);
		}

		// Assert
		Assert.Equal(5, buffer.Capacity);
		Assert.Equal(new[] { 998, 999, 1000, 1001, 1002 }, Items(buffer));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(2)]
	[InlineData(4)]
	public void Indexer_OutsideCount_Throws(int index)
	{
		// Arrange
		var buffer = new RingBuffer<int>(4);
		buffer.Add(10);
		buffer.Add(20);

		// Act
		var exception = Record.Exception(() => buffer[index]);

		// Assert
		Assert.IsType<ArgumentOutOfRangeException>(exception);
	}

	[Fact]
	public void Newest_OnEmptyBuffer_Throws()
	{
		// Arrange
		var buffer = new RingBuffer<int>(2);

		// Act
		var exception = Record.Exception(() => buffer.Newest);

		// Assert
		Assert.IsType<ArgumentOutOfRangeException>(exception);
	}

	[Fact]
	public void Clear_AfterWrapAround_StartsOver()
	{
		// Arrange
		var buffer = new RingBuffer<int>(3);
		for (var i = 0; i < 7; i++)
		{
			buffer.Add(i);
		}

		// Act
		buffer.Clear();
		buffer.Add(42);

		// Assert
		Assert.Equal(new[] { 42 }, Items(buffer));
	}

	[Fact]
	public void Constructor_ZeroCapacity_Throws()
	{
		// Act
		var exception = Record.Exception(() => new RingBuffer<int>(0));

		// Assert
		Assert.IsType<ArgumentOutOfRangeException>(exception);
	}

	private static int[] Items(RingBuffer<int> buffer)
	{
		var items = new int[buffer.Count];
		for (var i = 0; i < items.Length; i++)
		{
			items[i] = buffer[i];
		}

		return items;
	}
}
