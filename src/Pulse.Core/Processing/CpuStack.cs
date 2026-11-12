using Pulse.Core.Collections;
using Pulse.Core.Metrics;

namespace Pulse.Core.Processing;

/// <summary>
/// The stacked CPU graph: the processes that used the most CPU over the visible window each get a band of their own,
/// bottom up from the busiest, and everything else fills the rest up to the total CPU. A process that was not among the
/// busiest of a sample adds nothing there. Between two samples the bands run in a straight line.
/// </summary>
public sealed class CpuStack
{
	/// <summary>The processes with a band of their own; one more band holds everything else.</summary>
	public const int Named = 5;

	private CpuStack(IReadOnlyList<ProcessLoad> names) => Names = names;

	/// <summary>The processes with a band, busiest first, with their average CPU over the window.</summary>
	public IReadOnlyList<ProcessLoad> Names { get; }

	/// <summary>The bands for <paramref name="window"/>: the samples of the visible stretch of time.</summary>
	public static CpuStack Of(IReadOnlyCollection<MetricSnapshot> window) => new(ProcessCausation.AverageCpu(window, Named));

	/// <summary>
	/// The top of every band at <paramref name="time"/> in <paramref name="tops"/> (<see cref="Named"/> + 1 values, from
	/// the bottom band up; the last is the total CPU), from the samples of <paramref name="history"/> around it. False
	/// when there is no sample on both sides within <paramref name="gapSeconds"/>, or no CPU value.
	/// </summary>
	public bool TryTops(RingBuffer<MetricSnapshot> history, double time, double gapSeconds, Span<double> tops)
	{
		var after = LowerBound(history, time);
		if (after == 0 || after > history.Count)
		{
			return false;
		}

		var before = history[after - 1];
		if (after == history.Count)
		{
			// At or past the newest sample: hold it, as far as the gap allows.
			return time - before.Time <= gapSeconds && Fill(before, before, 0, tops);
		}

		var next = history[after];
		if (next.Time - before.Time > gapSeconds)
		{
			return false;
		}

		var amount = next.Time > before.Time ? (time - before.Time) / (next.Time - before.Time) : 0;
		return Fill(before, next, amount, tops);
	}

	/// <summary>The index of the first sample after <paramref name="time"/>.</summary>
	private static int LowerBound(RingBuffer<MetricSnapshot> history, double time)
	{
		int low = 0, high = history.Count;
		while (low < high)
		{
			var middle = (low + high) >>> 1;
			if (history[middle].Time <= time)
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

	private bool Fill(MetricSnapshot a, MetricSnapshot b, double amount, Span<double> tops)
	{
		var total = Lerp(a.CpuPercent, b.CpuPercent, amount);
		if (!double.IsFinite(total))
		{
			return false;
		}

		var sum = 0.0;
		for (var i = 0; i < Named; i++)
		{
			if (i < Names.Count)
			{
				sum += Lerp(LoadOf(a, Names[i].Name), LoadOf(b, Names[i].Name), amount);
			}

			tops[i] = sum;
		}

		tops[Named] = Math.Max(sum, total);
		return true;
	}

	private static double LoadOf(MetricSnapshot sample, string name)
	{
		foreach (var load in sample.TopCpu)
		{
			if (string.Equals(load.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return load.Value;
			}
		}

		return 0;
	}

	private static double Lerp(double a, double b, double amount)
		=> double.IsFinite(a) && double.IsFinite(b) ? a + ((b - a) * amount) : double.IsFinite(a) ? a : b;
}
