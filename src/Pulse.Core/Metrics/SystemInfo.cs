namespace Pulse.Core.Metrics;

/// <summary>What was found on the monitored machine when the collector started.</summary>
public sealed class SystemInfo
{
	public required string CpuName { get; init; }

	/// <summary>Logical processors; 0 when another machine did not say (the per-core count is used then).</summary>
	public required int LogicalProcessors { get; init; }
	public bool HasCpu { get; init; }
	public bool HasCpuCores { get; init; }
	public bool HasCpuTemperature { get; init; }

	public string? GpuName { get; init; }
	public double GpuDedicatedBytes { get; init; } = double.NaN;
	public bool HasGpu { get; init; }

	public bool HasMemory { get; init; }

	public bool HasDisk { get; init; }

	public string? NetworkAdapters { get; init; }
	public bool HasNetwork { get; init; }

	/// <summary>Claude Code transcripts or the Claude process tree can be read.</summary>
	public bool HasClaude { get; init; }

	/// <summary>The other machine read with <c>-host</c>; null for this machine.</summary>
	public string? Host { get; init; }

	/// <summary>The extra graphs asked for with <c>-process</c> and <c>-counter</c>, in order.</summary>
	public IReadOnlyList<WatchSpec> Watches { get; init; } = [];

	/// <summary>git activity is followed: in the repositories of <c>-git</c>, or in every repository Pulse finds.</summary>
	public bool FollowsGit { get; init; }

	/// <summary>The names of the repositories followed from the start (those of <c>-git</c>); more may be found later.</summary>
	public IReadOnlyList<string> GitRepositories { get; init; } = [];

	/// <summary>Errors from the Windows System and Application logs are followed.</summary>
	public bool HasWindowsEvents { get; init; }
}
