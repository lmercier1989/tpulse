using Pulse.Core.Metrics;
using Pulse.Core.Settings;

namespace Pulse.Core.Processing;

/// <summary>
/// The graphs, in display order: the seven built-in ones (their value is their place in the settings), then the extra
/// graphs of the command line.
/// </summary>
public enum MetricKind
{
	Cpu,
	Gpu,
	Memory,
	Disk,
	Network,
	Claude,

	/// <summary>The followed git repositories: lines changed and not committed yet.</summary>
	Git,

	/// <summary>A process followed with <c>-process</c>.</summary>
	Process,

	/// <summary>A performance counter followed with <c>-counter</c>.</summary>
	Counter,
}

/// <summary>Groups of <see cref="MetricKind"/>.</summary>
public static class MetricKinds
{
	/// <summary>The graphs every launch has, with keys 1 to 7 and remembered visibility and size.</summary>
	public static IReadOnlyList<MetricKind> Builtin { get; } =
		[MetricKind.Cpu, MetricKind.Gpu, MetricKind.Memory, MetricKind.Disk, MetricKind.Network, MetricKind.Claude, MetricKind.Git];
}

/// <summary>
/// The data behind one graph: a primary trace, an optional secondary trace (write, upload, VRAM), optional
/// per-core traces, the vertical scale and the min/max of the visible window. Used from the UI thread only.
/// </summary>
public sealed class Channel
{
	/// <summary>Enough samples for the longest history at the shortest interval, plus the display delay.</summary>
	public static readonly int Capacity = (int) Math.Ceiling(PulseSettings.MaxHistorySeconds / PulseSettings.MinIntervalSeconds) + 32;

	private readonly int _capacity;

	public Channel(MetricKind kind, AutoScale scale, bool hasSecondary, int capacity = 0)
	{
		_capacity = capacity > 0 ? capacity : Capacity;
		Kind = kind;
		Scale = scale;
		Primary = new Series(_capacity);
		Secondary = hasSecondary ? new Series(_capacity) : null;
	}

	public MetricKind Kind { get; }

	/// <summary>What an extra graph follows; null for a built-in graph.</summary>
	public WatchSpec? Watch { get; private init; }

	/// <summary>The position of <see cref="Watch"/> in <see cref="MetricSnapshot.Watches"/>.</summary>
	public int WatchIndex { get; private init; } = -1;

	/// <summary>
	/// The graph of <paramref name="kind"/>: percentages on a fixed 0 to 100 scale; disk (bytes/s), network (bits/s)
	/// and Claude (tokens/min) on an automatic scale with a floor (1 MB/s, 1 Mbps, 1,000 tokens/min), so idle noise is
	/// not blown up. CPU and memory carry the share of Claude as their second trace, on the same scale as the total.
	/// </summary>
	public static Channel Create(MetricKind kind, int capacity = 0) => kind switch
	{
		MetricKind.Cpu => new Channel(kind, AutoScale.Fixed(100), hasSecondary: true, capacity),
		MetricKind.Gpu => new Channel(kind, AutoScale.Fixed(100), hasSecondary: true, capacity),
		MetricKind.Memory => new Channel(kind, AutoScale.Fixed(100), hasSecondary: true, capacity),
		MetricKind.Disk => new Channel(kind, AutoScale.Automatic(1_000_000), hasSecondary: true, capacity),
		MetricKind.Network => new Channel(kind, AutoScale.Automatic(1_000_000), hasSecondary: true, capacity),
		MetricKind.Git => ForGit(capacity),
		_ => new Channel(kind, AutoScale.Automatic(1_000), hasSecondary: true, capacity),
	};

	/// <summary>
	/// The graph of an extra graph. A process draws its CPU and its memory, both in % of the machine, on a scale that
	/// zooms in (from 5 %) but stops at 100 %. A counter draws its value on a scale that suits its unit.
	/// </summary>
	public static Channel ForWatch(WatchSpec watch, int index, int capacity = 0)
	{
		if (watch.Kind == WatchKind.Process)
		{
			return new Channel(MetricKind.Process, AutoScale.Automatic(5, 100), hasSecondary: true, capacity) { Watch = watch, WatchIndex = index };
		}

		var scale = (watch.Counter?.Unit ?? CounterUnit.Number) switch
		{
			CounterUnit.Percent => AutoScale.Automatic(5, 100),
			CounterUnit.BytesPerSecond => AutoScale.Automatic(1_000),
			CounterUnit.Bytes => AutoScale.Automatic(1024 * 1024),
			_ => AutoScale.Automatic(1),
		};
		return new Channel(MetricKind.Counter, scale, hasSecondary: false, capacity) { Watch = watch, WatchIndex = index };
	}

	/// <summary>The git graph: the lines added and removed and not committed yet, over every followed repository.</summary>
	public static Channel ForGit(int capacity = 0) => new(MetricKind.Git, AutoScale.Automatic(10), hasSecondary: false, capacity);

	public AutoScale Scale { get; }

	public Series Primary { get; }

	public Series? Secondary { get; }

	/// <summary>Per-core traces (CPU only); created on the first per-core sample.</summary>
	public Series[] Cores { get; private set; } = [];

	/// <summary>Changes whenever data is added, so a renderer knows its geometry is stale.</summary>
	public int Version { get; private set; }

	/// <summary>Min/max of the primary trace over the visible window.</summary>
	public ValueRange PrimaryRange { get; private set; } = ValueRange.Empty;

	/// <summary>Min/max of the secondary trace over the visible window.</summary>
	public ValueRange SecondaryRange { get; private set; } = ValueRange.Empty;

	public void Append(double time, double primary, double secondary = double.NaN)
	{
		Primary.Add(time, primary);
		Secondary?.Add(time, secondary);
		Version++;
	}

	public void AppendCores(double time, ReadOnlySpan<double> values)
	{
		if (Cores.Length != values.Length)
		{
			Cores = new Series[values.Length];
			for (var i = 0; i < Cores.Length; i++)
			{
				Cores[i] = new Series(_capacity);
			}
		}

		for (var i = 0; i < values.Length; i++)
		{
			Cores[i].Add(time, values[i]);
		}

		Version++;
	}

	/// <summary>Appends every metric of <paramref name="sample"/> that belongs to this graph.</summary>
	public void Append(MetricSnapshot sample)
	{
		var time = sample.Time;
		switch (Kind)
		{
			case MetricKind.Cpu:
				Append(time, sample.CpuPercent, sample.ClaudeCpuPercent);
				if (sample.CpuCorePercents.Length > 0)
				{
					AppendCores(time, sample.CpuCorePercents);
				}

				break;
			case MetricKind.Gpu:
				Append(time, sample.GpuPercent, sample.GpuMemoryPercent);
				break;
			case MetricKind.Memory:
				Append(time, sample.MemoryPercent, sample.ClaudeMemoryPercent);
				break;
			case MetricKind.Disk:
				Append(time, sample.DiskReadBytesPerSecond, sample.DiskWriteBytesPerSecond);
				break;
			case MetricKind.Network:
				Append(time, sample.NetworkDownBitsPerSecond, sample.NetworkUpBitsPerSecond);
				break;
			case MetricKind.Git:
				Append(time, sample.GitChangedLines);
				break;
			case MetricKind.Process or MetricKind.Counter:
				var reading = WatchIndex >= 0 && WatchIndex < sample.Watches.Count ? sample.Watches[WatchIndex] : WatchReading.None;
				Append(time, reading.Value, reading.MemoryPercent);
				break;
			default:
				Append(time, sample.ClaudeOutputPerMinute, sample.ClaudeInputPerMinute);
				break;
		}
	}

	/// <summary>
	/// Whether every primary sample of the last <paramref name="seconds"/> before <paramref name="newestTime"/> is at or
	/// above <paramref name="threshold"/>, with at least <paramref name="seconds"/> of history: a lasting load, not a spike.
	/// </summary>
	public bool IsSustainedAbove(double threshold, double seconds, double newestTime)
	{
		var samples = Primary.Samples;
		if (samples.Count == 0 || samples[0].Time > newestTime - seconds)
		{
			return false;
		}

		for (var i = Primary.LowerBound(newestTime - seconds); i < samples.Count; i++)
		{
			if (!(samples[i].Value >= threshold))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>Breaks every trace at <paramref name="time"/>, for example after a pause.</summary>
	public void AppendBreak(double time)
	{
		Primary.Add(time, double.NaN);
		Secondary?.Add(time, double.NaN);
		foreach (var core in Cores)
		{
			core.Add(time, double.NaN);
		}

		Version++;
	}

	/// <summary>
	/// Recomputes the window statistics and feeds the scale. The window covers the visible history plus the
	/// samples still waiting behind the display delay, so the scale grows before a spike scrolls into view.
	/// </summary>
	public void Refresh(double newestTime, double historySeconds, double delaySeconds)
	{
		var from = newestTime - delaySeconds - historySeconds;
		PrimaryRange = Primary.Range(from, newestTime);
		SecondaryRange = Secondary?.Range(from, newestTime) ?? ValueRange.Empty;

		var peak = PrimaryRange.Max;
		if (!SecondaryRange.IsEmpty && (double.IsNaN(peak) || SecondaryRange.Max > peak))
		{
			peak = SecondaryRange.Max;
		}

		Scale.Observe(peak);
	}
}
