using Pulse.Core.Collections;

namespace Pulse.Core.Processing;

/// <summary>The history of one trace: samples in time order in a fixed-size ring buffer.</summary>
public sealed class Series(int capacity)
{
	public RingBuffer<Sample> Samples { get; } = new(capacity);

	/// <summary>The newest value, or NaN when there is none.</summary>
	public double Latest => Samples.Count == 0 ? double.NaN : Samples.Newest.Value;

	public void Add(double time, double value) => Samples.Add(new Sample(time, value));

	/// <summary>Index of the first sample at or after <paramref name="time"/>; <see cref="RingBuffer{T}.Count"/> when none.</summary>
	public int LowerBound(double time)
	{
		int low = 0, high = Samples.Count;
		while (low < high)
		{
			var middle = (low + high) >>> 1;
			if (Samples[middle].Time < time)
			{
				low = middle + 1;
			}
			else
			{
				high = middle;
			}
		}

		return low;
	}

	/// <summary>Minimum and maximum of the finite values with a time in [<paramref name="from"/>, <paramref name="to"/>].</summary>
	public ValueRange Range(double from, double to)
	{
		var min = double.PositiveInfinity;
		var max = double.NegativeInfinity;
		for (var i = LowerBound(from); i < Samples.Count; i++)
		{
			ref readonly var sample = ref Samples[i];
			if (sample.Time > to)
			{
				break;
			}

			if (double.IsFinite(sample.Value))
			{
				min = Math.Min(min, sample.Value);
				max = Math.Max(max, sample.Value);
			}
		}

		return double.IsPositiveInfinity(min) ? ValueRange.Empty : new ValueRange(min, max);
	}
}
