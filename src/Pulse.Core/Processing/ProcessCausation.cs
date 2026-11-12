using Pulse.Core.Metrics;

namespace Pulse.Core.Processing;

/// <summary>
/// Which processes carried the load over a stretch of time: the busiest process names of every sample, averaged over
/// the samples. A process that was not among the busiest of a sample counts as idle there, so a short spike weighs
/// less than a lasting load.
/// </summary>
public static class ProcessCausation
{
	/// <summary>The <paramref name="count"/> names with the highest average CPU over <paramref name="samples"/>, highest first.</summary>
	public static List<ProcessLoad> AverageCpu(IReadOnlyCollection<MetricSnapshot> samples, int count)
		=> Average(samples, sample => sample.TopCpu, count, 0.05);

	/// <summary>The <paramref name="count"/> names with the highest average I/O (bytes per second) over <paramref name="samples"/>.</summary>
	public static List<ProcessLoad> AverageIo(IReadOnlyCollection<MetricSnapshot> samples, int count)
		=> Average(samples, sample => sample.TopIo, count, 1000);

	private static List<ProcessLoad> Average(IReadOnlyCollection<MetricSnapshot> samples, Func<MetricSnapshot, IReadOnlyList<ProcessLoad>> loads, int count, double minimum)
	{
		if (samples.Count == 0)
		{
			return [];
		}

		var totals = new Dictionary<string, (double Sum, int Processes)>(StringComparer.OrdinalIgnoreCase);
		foreach (var sample in samples)
		{
			foreach (var load in loads(sample))
			{
				var (Sum, Processes) = totals.GetValueOrDefault(load.Name);
				totals[load.Name] = (Sum + load.Value, Math.Max(Processes, load.Count));
			}
		}

		return [.. totals
			.Select(pair => new ProcessLoad(pair.Key, pair.Value.Processes, pair.Value.Sum / samples.Count))
			.Where(load => load.Value >= minimum)
			.OrderByDescending(load => load.Value)
			.ThenBy(load => load.Name, StringComparer.OrdinalIgnoreCase)
			.Take(count)];
	}
}
