namespace Pulse.Core.Metrics;

/// <summary>
/// One sample of every metric. A value that is not available on this system, or not yet available
/// (rate counters need two samples), is <see cref="double.NaN"/>.
/// </summary>
public sealed class MetricSnapshot
{
	/// <summary>Monotonic sample time in seconds (<see cref="Diagnostics.MonotonicClock"/>).</summary>
	public required double Time { get; init; }

	public double CpuPercent { get; init; } = double.NaN;
	public double[] CpuCorePercents { get; init; } = [];
	public double CpuTemperatureCelsius { get; init; } = double.NaN;

	public double GpuPercent { get; init; } = double.NaN;
	public double GpuMemoryUsedBytes { get; init; } = double.NaN;
	public double GpuMemoryPercent { get; init; } = double.NaN;
	public double GpuTemperatureCelsius { get; init; } = double.NaN;
	public double GpuPowerWatts { get; init; } = double.NaN;

	public double MemoryUsedBytes { get; init; } = double.NaN;
	public double MemoryAvailableBytes { get; init; } = double.NaN;
	public double MemoryTotalBytes { get; init; } = double.NaN;
	public double MemoryPercent { get; init; } = double.NaN;

	public double DiskReadBytesPerSecond { get; init; } = double.NaN;
	public double DiskWriteBytesPerSecond { get; init; } = double.NaN;
	public double DiskActivePercent { get; init; } = double.NaN;

	public double NetworkDownBitsPerSecond { get; init; } = double.NaN;
	public double NetworkUpBitsPerSecond { get; init; } = double.NaN;

	public double ProcessCount { get; init; } = double.NaN;
	public double ThreadCount { get; init; } = double.NaN;

	/// <summary>Time since the monitored machine started, in seconds.</summary>
	public double UptimeSeconds { get; init; } = double.NaN;

	/// <summary>Disk space, network addresses and system facts; refreshed every few seconds, null until first read.</summary>
	public SystemDetails? Details { get; init; }

	/// <summary>
	/// Round trip of the last ping, in milliseconds, to the default gateway (or to the other machine with <c>-host</c>);
	/// NaN when it did not answer.
	/// </summary>
	public double PingMs { get; init; } = double.NaN;

	/// <summary>One reading per <see cref="SystemInfo.Watches"/> entry, in the same order.</summary>
	public IReadOnlyList<WatchReading> Watches { get; init; } = [];

	/// <summary>Events that happened since the previous sample: git activity, Windows errors.</summary>
	public IReadOnlyList<PulseEvent> Events { get; init; } = [];

	/// <summary>The followed git repositories; null without <c>-git</c>.</summary>
	public GitActivity? Git { get; init; }

	/// <summary>Lines added and removed and not committed yet, over every followed repository; NaN without <c>-git</c>.</summary>
	public double GitChangedLines { get; init; } = double.NaN;

	/// <summary>The process names with the most CPU right now, most first.</summary>
	public IReadOnlyList<ProcessLoad> TopCpu { get; init; } = [];

	/// <summary>The process names with the most I/O (reads and writes, bytes per second) right now, most first.</summary>
	public IReadOnlyList<ProcessLoad> TopIo { get; init; } = [];

	/// <summary>Processes whose memory kept growing over the last ten minutes, fastest first; empty on another machine.</summary>
	public IReadOnlyList<LeakSuspect> Leaks { get; init; } = [];

	/// <summary>The TCP ports programs listen on, refreshed every few seconds; null on another machine and in a recording.</summary>
	public PortActivity? Ports { get; init; }

	/// <summary>The wall-clock time of the sample when it is played back from a recording; null when live.</summary>
	public DateTimeOffset? RecordedAt { get; init; }

	/// <summary>What the Claude app, its sessions and their tools cost the machine.</summary>
	public ClaudeProcessUsage ClaudeProcesses { get; init; } = ClaudeProcessUsage.None;

	/// <summary>CPU of the Claude app, its sessions and their tools, in % of the machine.</summary>
	public double ClaudeCpuPercent { get; init; } = double.NaN;

	/// <summary>Private memory of the Claude app, its sessions and their tools, in % of physical memory.</summary>
	public double ClaudeMemoryPercent { get; init; } = double.NaN;

	/// <summary>Tokens of the answers written since the previous sample.</summary>
	public TokenCounts ClaudeTokens { get; init; }

	/// <summary>Output tokens per minute over the last sample interval.</summary>
	public double ClaudeOutputPerMinute { get; init; } = double.NaN;

	/// <summary>Fresh input tokens (uncached plus cache writes) per minute over the last sample interval.</summary>
	public double ClaudeInputPerMinute { get; init; } = double.NaN;

	/// <summary>Tokens counted since Pulse started.</summary>
	public TokenCounts ClaudeTotal { get; init; }

	/// <summary>Claude Code sessions with an answer in the last 10 minutes, latest first.</summary>
	public IReadOnlyList<ClaudeSession> ClaudeSessions { get; init; } = [];
}
