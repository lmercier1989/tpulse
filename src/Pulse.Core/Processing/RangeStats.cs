namespace Pulse.Core.Processing;

/// <summary>The lowest, average, 95th percentile and highest value of a trace over a range of time.</summary>
/// <param name="Min">The lowest value.</param>
/// <param name="Average">The mean of the samples.</param>
/// <param name="P95">95 % of the samples are at or below this value (nearest rank).</param>
/// <param name="Max">The highest value.</param>
/// <param name="Count">The samples with a value; 0 when the range has none.</param>
public readonly record struct RangeStats(double Min, double Average, double P95, double Max, int Count)
{
	public static readonly RangeStats Empty = new(double.NaN, double.NaN, double.NaN, double.NaN, 0);

	public bool IsEmpty => Count == 0;

	/// <summary>The statistics of the finite values of <paramref name="series"/> with a time in [<paramref name="from"/>, <paramref name="to"/>].</summary>
	public static RangeStats Of(Series series, double from, double to)
	{
		var values = new List<double>();
		var samples = series.Samples;
		for (var i = series.LowerBound(from); i < samples.Count && samples[i].Time <= to; i++)
		{
			if (double.IsFinite(samples[i].Value))
			{
				values.Add(samples[i].Value);
			}
		}

		return Of(values);
	}

	/// <summary>The statistics of <paramref name="values"/>, which all have to be finite.</summary>
	public static RangeStats Of(IReadOnlyCollection<double> values)
	{
		if (values.Count == 0)
		{
			return Empty;
		}

		var sorted = values.Order().ToArray();
		var rank = Math.Clamp((int) Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1);
		return new RangeStats(sorted[0], sorted.Average(), sorted[rank], sorted[^1], sorted.Length);
	}
}
