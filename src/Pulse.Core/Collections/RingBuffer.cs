namespace Pulse.Core.Collections;

/// <summary>
/// A fixed-capacity circular buffer. Once full, every <see cref="Add"/> overwrites the oldest item, so the
/// memory used never changes after construction. Index 0 is the oldest item, <see cref="Count"/> - 1 the newest.
/// </summary>
public sealed class RingBuffer<T>
{
	private readonly T[] _items;
	private int _head;
	private int _count;

	public RingBuffer(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
		_items = new T[capacity];
	}

	public int Capacity => _items.Length;

	public int Count => _count;

	public bool IsFull => _count == _items.Length;

	/// <summary>The item at <paramref name="index"/>, counted from the oldest.</summary>
	public ref readonly T this[int index]
	{
		get
		{
			if ((uint) index >= (uint) _count)
			{
				throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be below {_count}.");
			}

			return ref _items[Wrap(_head + index)];
		}
	}

	/// <summary>The most recently added item.</summary>
	public ref readonly T Newest => ref this[_count - 1];

	public void Add(T item)
	{
		if (_count < _items.Length)
		{
			_items[Wrap(_head + _count)] = item;
			_count++;
			return;
		}

		_items[_head] = item;
		_head = Wrap(_head + 1);
	}

	public void Clear()
	{
		Array.Clear(_items);
		_head = 0;
		_count = 0;
	}

	private int Wrap(int index) => index >= _items.Length ? index - _items.Length : index;
}
