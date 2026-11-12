using Pulse.Core.Formatting;

namespace Pulse.Core.Metrics;

/// <summary>
/// Finds the moments a graph jumps well above its own recent level, and the process that caused the jump: a CPU spike
/// names the process whose CPU rose most compared to the samples before, a disk spike the process whose I/O rose most.
/// The level is the median of the last <see cref="BaselineSamples"/> samples, so a lasting load becomes the new level
/// rather than one long spike.
/// </summary>
internal sealed class SpikeDetector
{
	internal const int BaselineSamples = 30;
	internal const int MinimumSamples = 10;

	/// <summary>A graph reports at most one spike in this many seconds.</summary>
	internal const double QuietSeconds = 20;

	private readonly Rule[] _rules =
	[
		new("CPU", sample => sample.CpuPercent, Percent: true, Floor: 50, Rise: 30, sample => sample.TopCpu, value => Units.Percent(value)),
		new("GPU", sample => sample.GpuPercent, Percent: true, Floor: 50, Rise: 30, null, value => Units.Percent(value)),
		new("Disk", sample => sample.DiskReadBytesPerSecond + sample.DiskWriteBytesPerSecond, Percent: false, Floor: 50e6, Rise: 10e6, sample => sample.TopIo, value => Units.BytesPerSecond(value)),
		new("Network", sample => sample.NetworkDownBitsPerSecond + sample.NetworkUpBitsPerSecond, Percent: false, Floor: 50e6, Rise: 10e6, null, value => Units.BitsPerSecond(value)),
	];

	/// <summary>Adds a <see cref="EventKind.Spike"/> event to <paramref name="events"/> for every graph that jumps at <paramref name="sample"/>.</summary>
	public void Check(MetricSnapshot sample, DateTimeOffset wallNow, List<PulseEvent> events)
	{
		foreach (var rule in _rules)
		{
			var value = rule.Value(sample);
			if (!double.IsFinite(value))
			{
				continue;
			}

			if (rule.Recent.Count >= MinimumSamples)
			{
				var baseline = Median(rule.Recent);
				var threshold = rule.Percent ? Math.Max(rule.Floor, baseline + rule.Rise) : Math.Max(rule.Floor, (baseline * 4) + rule.Rise);
				if (rule.High && value < baseline + ((threshold - baseline) / 2))
				{
					rule.High = false;
				}

				if (!rule.High && value >= threshold && sample.Time - rule.LastSpike >= QuietSeconds)
				{
					rule.High = true;
					rule.LastSpike = sample.Time;
					events.Add(new PulseEvent(sample.Time, wallNow, EventKind.Spike, rule.Source, "spike", Describe(rule, sample, value, baseline)));
				}
			}

			rule.Recent.Enqueue(value);
			if (rule.Recent.Count > BaselineSamples)
			{
				rule.Recent.Dequeue();
			}

			if (rule.Loads is not null)
			{
				rule.RecentLoads.Enqueue(rule.Loads(sample));
				if (rule.RecentLoads.Count > BaselineSamples)
				{
					rule.RecentLoads.Dequeue();
				}
			}
		}
	}

	/// <summary>"92% (usually 18%) · dotnet ×7 +61%".</summary>
	private static string Describe(Rule rule, MetricSnapshot sample, double value, double baseline)
	{
		var text = $"{rule.Format(value)} (usually {rule.Format(baseline)})";
		if (rule.Loads is null || Cause(rule.Loads(sample), rule.RecentLoads) is not { } cause)
		{
			return text;
		}

		var name = cause.Load.Count > 1 ? $"{cause.Load.Name} ×{cause.Load.Count}" : cause.Load.Name;
		return $"{text} · {name} +{rule.Format(cause.Rise)}";
	}

	/// <summary>The process whose load rose most compared to its average over <paramref name="before"/> (absent counts as 0).</summary>
	internal static (ProcessLoad Load, double Rise)? Cause(IReadOnlyList<ProcessLoad> now, IReadOnlyCollection<IReadOnlyList<ProcessLoad>> before)
	{
		(ProcessLoad Load, double Rise)? best = null;
		foreach (var load in now)
		{
			var earlier = before.Count == 0 ? 0 : before.Sum(loads => loads.FirstOrDefault(item => item.Name == load.Name)?.Value ?? 0) / before.Count;
			var rise = load.Value - earlier;
			if (rise > 0 && (best is null || rise > best.Value.Rise))
			{
				best = (load, rise);
			}
		}

		return best;
	}

	internal static double Median(IEnumerable<double> values)
	{
		var sorted = values.Order().ToArray();
		return sorted.Length == 0 ? double.NaN
			: sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
			: (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
	}

	/// <summary>
	/// How one graph spikes: <paramref name="Percent"/> graphs need to rise <paramref name="Rise"/> points above their level
	/// and reach <paramref name="Floor"/>; rate graphs need four times their level plus <paramref name="Rise"/>, and at least
	/// <paramref name="Floor"/>.
	/// </summary>
	private sealed record Rule(
		string Source,
		Func<MetricSnapshot, double> Value,
		bool Percent,
		double Floor,
		double Rise,
		Func<MetricSnapshot, IReadOnlyList<ProcessLoad>>? Loads,
		Func<double, string> Format)
	{
		public Queue<double> Recent { get; } = new();

		public Queue<IReadOnlyList<ProcessLoad>> RecentLoads { get; } = new();

		public bool High { get; set; }

		public double LastSpike { get; set; } = double.NegativeInfinity;
	}
}
