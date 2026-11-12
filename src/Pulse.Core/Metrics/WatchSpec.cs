using System.Globalization;

namespace Pulse.Core.Metrics;

/// <summary>What an extra graph follows: one process (by name or id), or any performance counter.</summary>
public enum WatchKind
{
	Process,
	Counter,
}

/// <summary>
/// An extra graph asked for on the command line: <c>-process Shop.Api</c> or
/// <c>-counter "\Web Service(_Total)\Current Connections"</c>.
/// </summary>
/// <param name="Kind">A process or a counter.</param>
/// <param name="Target">The process name (with or without .exe) or id, or the counter path as typed.</param>
public sealed record WatchSpec(WatchKind Kind, string Target)
{
	/// <summary>At most this many extra graphs; they take the keys 7, 8 and 9.</summary>
	public const int MaximumCount = 3;

	/// <summary>The process name without ".exe", as Windows lists it; empty for a counter.</summary>
	public string ProcessName => Kind == WatchKind.Process && ProcessId is null
		? (Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Target[..^4] : Target)
		: string.Empty;

	/// <summary>The process id when the target is a number.</summary>
	public int? ProcessId => Kind == WatchKind.Process && int.TryParse(Target, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

	/// <summary>The parsed counter path; null for a process.</summary>
	public CounterPath? Counter => Kind == WatchKind.Counter && CounterPath.TryParse(Target, out var path) ? path : null;

	/// <summary>The graph title: the process name ("pid 1234" for an id), or the counter name.</summary>
	public string Title => Kind == WatchKind.Process
		? ProcessId is { } id ? $"pid {id}" : ProcessName
		: Counter?.Counter ?? Target;
}

/// <summary>
/// One reading of a <see cref="WatchSpec"/>. For a process: <see cref="Value"/> is the CPU of all its processes in % of
/// the machine, with their private memory, process count, threads and handles. For a counter: <see cref="Value"/> is the
/// counter (summed over its instances when the path has a wildcard) and <see cref="Count"/> the instances read.
/// </summary>
public readonly record struct WatchReading(double Value, double MemoryBytes, double MemoryPercent, int Count, double Threads, double Handles)
{
	public static WatchReading None { get; } = new(double.NaN, double.NaN, double.NaN, 0, double.NaN, double.NaN);

	/// <summary>A counter reading.</summary>
	public static WatchReading OfCounter(double value, int instances) => None with { Value = value, Count = instances };
}
