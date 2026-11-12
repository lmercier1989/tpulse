using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

using Pulse.Core.Diagnostics;
using Pulse.Core.Formatting;
using Pulse.Core.Interop;
using Pulse.Core.Processing;

namespace Pulse.Core.Metrics;

/// <summary>
/// Reads every metric of this Linux machine from the kernel: <c>/proc</c> for CPU, memory, disks, network and processes,
/// <c>/sys</c> for temperatures, the GPU and the battery, the systemd journal for errors. Each source is probed once at
/// start-up; a source that is missing or fails is logged and reported as NaN, so one broken sensor never stops the others.
/// Reading another machine (<c>-host</c>) and performance counters (<c>-counter</c>) need Windows. Not thread-safe:
/// <see cref="Sampler"/> calls <see cref="Collect"/> from one loop only.
/// </summary>
public sealed class SystemMetricsCollector : IDisposable
{
	private const double DetailsRefreshSeconds = 5;
	private const double SectorBytes = 512;

	private readonly Lock _gate = new();
	private WatchSpec[] _watches;
	private readonly GitMonitor? _git;
	private readonly JournalEventSource? _journal;
	private readonly TopProcesses? _top;
	private readonly LeakDetector? _leaks;
	private readonly SpikeDetector _spikes = new();
	private readonly MarkLog _marks = new(MarkLog.DefaultPath());
	private readonly List<PulseEvent> _newEvents = [];
	private readonly LinuxGpu? _gpu;
	private readonly string? _thermal;
	private readonly HashSet<string> _disks;
	private readonly Dictionary<string, ulong> _diskBusy = new(StringComparer.Ordinal);
	private readonly CounterRate _diskRate = new();
	private readonly CounterRate _networkRate = new();
	private readonly List<CpuTimes> _cores = [];
	private CpuTimes _lastCpu;
	private CpuTimes[] _lastCores = [];
	private Dictionary<string, ulong> _countedInterfaces = new(StringComparer.Ordinal);
	private readonly ProcessSnapshot? _snapshot;
	private readonly ClaudeProcesses? _claudeProcesses;
	private ProcessWatches? _processWatches;
	private readonly ClaudeTranscripts? _claudeTranscripts;
	private readonly Ping _ping = new();
	private Task<PingReply>? _pingReply;
	private double _pingMs = double.NaN;
	private SystemDetails? _details;
	private double _detailsReadAt = double.NegativeInfinity;
	private double _lastCollect = double.NaN;
	private long _lastDiskTimestamp;
	private long _lastNetworkTimestamp;
	private bool _disposed;

	private SystemMetricsCollector(string? host, IReadOnlyList<WatchSpec> watches, IReadOnlyList<string>? gitPaths)
	{
		if (!string.IsNullOrWhiteSpace(host))
		{
			Log.Warn($"Reading another machine ({host}) needs Windows; showing this machine.");
		}

		_watches = [.. watches];
		var cpuName = Guard("cpu-name", () => HardwareNames.CleanCpuName(LinuxProc.CpuName(File.ReadAllText("/proc/cpuinfo"))), HardwareNames.CleanCpuName(string.Empty));
		var hasCpu = Guard("cpu", () => ReadCpu(out _).Total > 0, false);
		_thermal = Guard("thermal-find", FindCpuTemperature, null);
		Log.Info(_thermal is null ? "No CPU temperature sensor found." : $"CPU temperature from {_thermal}.");
		_disks = Guard("disk-find", FindPhysicalDisks, []);
		Log.Info(_disks.Count == 0 ? "No physical disk found." : $"Disks counted: {string.Join(", ", _disks.Order(StringComparer.Ordinal))}");
		_gpu = Guard("gpu", LinuxGpu.TryOpen, null);

		if (ProcessSnapshot.IsSupported)
		{
			_snapshot = new ProcessSnapshot
			{
				WatchedNames = ProcessWatches.ImageNames(_watches),
				WatchedIds = [.. _watches.Select(watch => watch.ProcessId ?? 0).Where(id => id > 0)],
			};
			_claudeProcesses = new ClaudeProcesses();
			if (_watches.Any(watch => watch.Kind == WatchKind.Process))
			{
				_processWatches = new ProcessWatches(_watches);
			}

			_top = new TopProcesses();
			_leaks = new LeakDetector();
		}

		_claudeTranscripts = Guard("claude-transcripts", ClaudeTranscripts.TryCreate, null);
		_journal = Guard("journal", JournalEventSource.TryCreate, null);
		_countedInterfaces = Guard("network-describe", FindCountedInterfaces, _countedInterfaces);
		var adapters = _countedInterfaces.Count == 0 ? null : string.Join(" + ", _countedInterfaces.Keys.Order(StringComparer.Ordinal));
		Log.Info(adapters is null ? "No physical network interface is up." : $"Network interfaces counted: {adapters}");

		if (gitPaths is { Count: > 0 } && Guard("git-find", () => GitMonitor.Find(gitPaths), []) is { Count: > 0 } repositories)
		{
			_git = new GitMonitor(repositories);
			Log.Info($"Following git in {string.Join(", ", repositories.Select(repository => repository.Root))}.");
		}
		else if (gitPaths is { Count: 0 })
		{
			_git = Guard("git-discover", () => GitMonitor.Discovering(Environment.CurrentDirectory), null);
			Log.Info("Following git in every repository found on this machine.");
		}

		Info = new SystemInfo
		{
			CpuName = cpuName,
			LogicalProcessors = Environment.ProcessorCount,
			HasCpu = hasCpu,
			HasCpuCores = hasCpu,
			HasCpuTemperature = _thermal is not null,
			GpuName = _gpu?.Name,
			GpuDedicatedBytes = _gpu is { DedicatedBytes: > 0 } gpu ? gpu.DedicatedBytes : double.NaN,
			HasGpu = _gpu is not null,
			HasMemory = true,
			HasDisk = _disks.Count > 0,
			NetworkAdapters = adapters,
			HasNetwork = true,
			HasClaude = _claudeProcesses is not null || _claudeTranscripts is not null,
			Host = null,
			Watches = _watches,
			FollowsGit = _git is not null,
			GitRepositories = [.. _git?.Repositories.Select(repository => repository.Name) ?? []],
			HasWindowsEvents = _journal is not null,
		};
	}

	public SystemInfo Info { get; }

	/// <summary>This machine, without extra graphs.</summary>
	public static SystemMetricsCollector Create() => new(null, [], null);

	/// <summary>This machine with the extra graphs of <paramref name="watches"/>; <paramref name="host"/> needs Windows.</summary>
	public static SystemMetricsCollector Create(string? host, IReadOnlyList<WatchSpec> watches) => new(host, watches, null);

	/// <summary>
	/// This machine with the extra graphs of <paramref name="watches"/>, following git: in the repositories of
	/// <paramref name="gitPaths"/> (a folder in a repository, or a folder of repositories), in every repository Pulse finds
	/// itself when the list is empty, or not at all when it is null.
	/// </summary>
	public static SystemMetricsCollector Create(string? host, IReadOnlyList<WatchSpec> watches, IReadOnlyList<string>? gitPaths) => new(host, watches, gitPaths);

	/// <summary>The names of the git repositories <c>-git</c> finds for <paramref name="path"/>.</summary>
	public static IReadOnlyList<string> GitRepositoriesAt(string path) => [.. GitRepository.Find(path).Select(repository => repository.Name)];

	/// <summary>
	/// Checks before start-up what can be read; returns what is wrong, in words for the person who typed the command, or
	/// null when everything can be read. Another machine and performance counters are Windows features.
	/// </summary>
	public static string? Check(string? host, IReadOnlyList<WatchSpec> watches, IReadOnlyList<string>? gitPaths = null)
	{
		foreach (var path in gitPaths ?? [])
		{
			var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
			if (GitRepository.Find(full).Count == 0)
			{
				return $"There is no git repository at {full}, nor directly inside it.";
			}
		}

		if (!string.IsNullOrWhiteSpace(host))
		{
			return "Reading another machine with -host uses Windows performance counters, so it works on Windows only.";
		}

		return watches.Any(watch => watch.Kind == WatchKind.Counter)
			? "Performance counters (-counter) exist on Windows only. On Linux, follow a program with -process <name>."
			: null;
	}

	/// <summary>
	/// Follows the processes named <paramref name="name"/> from the next sample on, as one more extra graph, up to
	/// <see cref="WatchSpec.MaximumCount"/> extra graphs. <paramref name="index"/> is where its readings stand in
	/// <see cref="MetricSnapshot.Watches"/>.
	/// </summary>
	public bool TryFollowProcess(string name, out WatchSpec watch, out int index)
	{
		watch = new WatchSpec(WatchKind.Process, name);
		index = -1;
		lock (_gate)
		{
			if (_snapshot is null || _watches.Length >= WatchSpec.MaximumCount
				|| _watches.Any(existing => existing.Kind == WatchKind.Process && string.Equals(existing.Target, name, StringComparison.OrdinalIgnoreCase)))
			{
				return false;
			}

			_watches = [.. _watches, watch];
			_snapshot.WatchedNames = ProcessWatches.ImageNames(_watches);
			_processWatches = new ProcessWatches(_watches);
			index = _watches.Length - 1;
			return true;
		}
	}

	/// <summary>Samples every source once.</summary>
	public MetricSnapshot Collect()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			return CollectLocked();
		}
	}

	/// <summary>One sample of every source, while no process is added to the ones followed.</summary>
	private MetricSnapshot CollectLocked()
	{
		var time = MonotonicClock.Now;
		var (cpu, cores) = Guard("cpu", SampleCpu, (double.NaN, []));
		var (memoryTotal, memoryAvailable) = Guard("memory", ReadMemory, (double.NaN, double.NaN));
		var memoryUsed = memoryTotal - memoryAvailable;
		var (diskRead, diskWrite, diskActive) = Guard("disk", SampleDisks, (double.NaN, double.NaN, double.NaN));
		var (down, up) = Guard("network", SampleNetwork, (double.NaN, double.NaN));
		var gpuMemoryUsed = _gpu is null ? double.NaN : Guard("gpu-memory", _gpu.ReadMemoryUsedBytes, double.NaN);
		if (time - _detailsReadAt >= DetailsRefreshSeconds)
		{
			_details = SystemDetailsReader.Read();
			_detailsReadAt = time;
			_countedInterfaces = Guard("network-describe", FindCountedInterfaces, _countedInterfaces);
			Guard("ping", Ping, false);
		}

		var records = TakeSnapshot();
		var threads = 0L;
		foreach (ref readonly var record in records)
		{
			threads += record.Threads;
		}

		var claude = SampleClaude(records);
		var watches = ReadWatches(records, memoryTotal);
		var (topCpu, topIo) = SampleTop(records);
		_newEvents.Clear();
		var wallNow = TimeProvider.System.GetUtcNow();
		var git = PollGit(time, wallNow, records);
		_journal?.Drain(time, wallNow, _newEvents);
		Guard("marks", () => Drain(_marks, time, wallNow), false);
		var leaks = SampleLeaks(records, time, wallNow);
		var (tokens, sessions) = _claudeTranscripts is null
			? (default, [])
			: Guard("claude-transcripts", () => _claudeTranscripts.Poll(time), (default(TokenCounts), (IReadOnlyList<ClaudeSession>) []));
		var elapsed = time - _lastCollect;
		_lastCollect = time;
		var perMinute = elapsed > 0 ? 60 / elapsed : double.NaN;

		var events = new List<PulseEvent>(_newEvents);
		var snapshot = new MetricSnapshot
		{
			Time = time,
			CpuPercent = Clamp(cpu),
			CpuCorePercents = cores,
			CpuTemperatureCelsius = Guard("thermal", ReadThermal, double.NaN),
			GpuPercent = _gpu is null ? double.NaN : Clamp(Guard("gpu-load", _gpu.ReadUtilizationPercent, double.NaN)),
			GpuMemoryUsedBytes = gpuMemoryUsed,
			GpuMemoryPercent = Info.GpuDedicatedBytes > 0 ? Clamp(gpuMemoryUsed / Info.GpuDedicatedBytes * 100) : double.NaN,
			GpuTemperatureCelsius = _gpu is null ? double.NaN : Guard("gpu-temperature", _gpu.ReadTemperatureCelsius, double.NaN),
			GpuPowerWatts = _gpu is null ? double.NaN : Guard("gpu-power", _gpu.ReadPowerWatts, double.NaN),
			MemoryTotalBytes = memoryTotal,
			MemoryAvailableBytes = memoryAvailable,
			MemoryUsedBytes = memoryUsed,
			MemoryPercent = memoryTotal > 0 ? Clamp(memoryUsed / memoryTotal * 100) : double.NaN,
			DiskReadBytesPerSecond = diskRead,
			DiskWriteBytesPerSecond = diskWrite,
			DiskActivePercent = diskActive,
			NetworkDownBitsPerSecond = down,
			NetworkUpBitsPerSecond = up,
			ProcessCount = records.IsEmpty ? double.NaN : records.Length,
			ThreadCount = records.IsEmpty ? double.NaN : threads,
			UptimeSeconds = Guard("uptime", ReadUptime, Environment.TickCount64 / 1000.0),
			Details = _details,
			PingMs = _pingMs,
			Watches = watches,
			ClaudeProcesses = claude,
			ClaudeCpuPercent = Clamp(claude.TotalCpu),
			ClaudeMemoryPercent = memoryTotal > 0 && double.IsFinite(claude.TotalBytes) ? Clamp(claude.TotalBytes / memoryTotal * 100) : double.NaN,
			ClaudeTokens = tokens,
			ClaudeOutputPerMinute = _claudeTranscripts is null ? double.NaN : tokens.Output * perMinute,
			ClaudeInputPerMinute = _claudeTranscripts is null ? double.NaN : tokens.FreshInput * perMinute,
			ClaudeTotal = _claudeTranscripts?.Total ?? default,
			ClaudeSessions = sessions,
			Events = events,
			Git = git,
			GitChangedLines = git?.ChangedLines ?? double.NaN,
			TopCpu = topCpu,
			TopIo = topIo,
			Leaks = leaks,
		};

		// A spike is judged on the finished sample, and its event joins the sample it happened in.
		Guard("spikes", () => CheckSpikes(snapshot, wallNow, events), false);
		return snapshot;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_claudeTranscripts?.Dispose();
		_ping.Dispose();
		_journal?.Dispose();
		_gpu?.Dispose();
	}

	/// <summary>
	/// The CPU temperature sensor: the die of an AMD (k10temp, zenpower) or Intel (coretemp package) processor, the CPU zone
	/// of an ARM board, else an ACPI thermal zone. The path of its millidegree file, or null.
	/// </summary>
	private static string? FindCpuTemperature()
	{
		const string Hwmon = "/sys/class/hwmon";
		var wanted = new (string Driver, string? Label)[]
		{
			("k10temp", "Tdie"), ("k10temp", "Tctl"), ("zenpower", "Tdie"), ("zenpower", "Tctl"), ("coretemp", "Package id 0"),
			("cpu_thermal", null), ("cpu-thermal", null), ("soc_thermal", null),
		};

		if (Directory.Exists(Hwmon))
		{
			var sensors = Directory.EnumerateDirectories(Hwmon).Select(folder => (Folder: folder, Driver: Read(Path.Combine(folder, "name")))).ToList();
			foreach (var (driver, label) in wanted)
			{
				foreach (var sensor in sensors.Where(sensor => sensor.Driver == driver))
				{
					for (var i = 1; i <= 16; i++)
					{
						var input = Path.Combine(sensor.Folder, $"temp{i}_input");
						if (File.Exists(input) && (label is null || Read(Path.Combine(sensor.Folder, $"temp{i}_label")) == label))
						{
							return input;
						}
					}
				}
			}
		}

		const string Thermal = "/sys/class/thermal";
		if (Directory.Exists(Thermal))
		{
			var zones = Directory.EnumerateDirectories(Thermal, "thermal_zone*").Select(folder => (Folder: folder, Type: Read(Path.Combine(folder, "type")))).ToList();
			foreach (var type in new[] { "x86_pkg_temp", "cpu-thermal", "cpu_thermal", "soc-thermal", "acpitz" })
			{
				if (zones.FirstOrDefault(zone => zone.Type == type) is { Folder: not null } zone)
				{
					return Path.Combine(zone.Folder, "temp");
				}
			}
		}

		return null;
	}

	/// <summary>The block devices of hardware (with a device behind them): not partitions, loop, zram, device-mapper or RAID.</summary>
	private static HashSet<string> FindPhysicalDisks()
		=> [.. Directory.EnumerateDirectories("/sys/block")
			.Where(folder => Directory.Exists(Path.Combine(folder, "device")))
			.Select(Path.GetFileName)
			.OfType<string>()];

	/// <summary>
	/// The network interfaces of hardware that are up, with their index: virtual bridges, containers, VPN tunnels and
	/// loopback carry traffic that also passes a physical interface, and summing them would count it twice.
	/// </summary>
	private static Dictionary<string, ulong> FindCountedInterfaces()
	{
		var counted = new Dictionary<string, ulong>(StringComparer.Ordinal);
		foreach (var folder in Directory.EnumerateDirectories("/sys/class/net"))
		{
			if (Directory.Exists(Path.Combine(folder, "device")) && Read(Path.Combine(folder, "operstate")) == "up")
			{
				counted[Path.GetFileName(folder)] = LinuxProc.ParseULong(Read(Path.Combine(folder, "ifindex")));
			}
		}

		return counted;
	}

	private static string? Read(string path)
	{
		try
		{
			return File.ReadAllText(path).Trim();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static (double Total, double Available) ReadMemory()
	{
		var text = File.ReadAllText("/proc/meminfo");
		return (LinuxProc.MemInfo(text, "MemTotal"), LinuxProc.MemInfo(text, "MemAvailable"));
	}

	private static double ReadUptime()
	{
		var text = File.ReadAllText("/proc/uptime");
		var space = text.IndexOf(' ', StringComparison.Ordinal);
		return double.Parse(space < 0 ? text : text[..space], NumberStyles.Float, CultureInfo.InvariantCulture);
	}

	private CpuTimes ReadCpu(out List<CpuTimes> cores)
	{
		var total = LinuxProc.ParseCpu(File.ReadAllText("/proc/stat"), _cores, out _);
		cores = _cores;
		return total;
	}

	/// <summary>The machine and every core in %, from the change of the busy and total ticks since the previous sample.</summary>
	private (double Total, double[] Cores) SampleCpu()
	{
		var total = ReadCpu(out var cores);
		var percent = Percent(_lastCpu, total);
		var values = new double[cores.Count];
		for (var i = 0; i < values.Length; i++)
		{
			values[i] = i < _lastCores.Length ? Clamp(Percent(_lastCores[i], cores[i])) : double.NaN;
		}

		_lastCpu = total;
		_lastCores = [.. cores];
		return (percent, values);

		static double Percent(CpuTimes before, CpuTimes now)
			=> before.Total == 0 || now.Total <= before.Total
				? double.NaN
				: (now.Busy >= before.Busy ? now.Busy - before.Busy : 0) * 100.0 / (now.Total - before.Total);
	}

	/// <summary>Read and write throughput over the physical disks, and the busiest disk in % of the time it was busy.</summary>
	private (double Read, double Write, double Active) SampleDisks()
	{
		if (_disks.Count == 0)
		{
			return (double.NaN, double.NaN, double.NaN);
		}

		var timestamp = Stopwatch.GetTimestamp();
		var elapsed = _lastDiskTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastDiskTimestamp, timestamp).TotalSeconds;
		_lastDiskTimestamp = timestamp;
		_diskRate.Begin();
		var busiest = double.NaN;
		foreach (var disk in LinuxProc.ParseDiskStats(File.ReadAllText("/proc/diskstats"), _disks.Contains))
		{
			_diskRate.Add((ulong) disk.Name.GetHashCode(StringComparison.Ordinal), disk.SectorsRead, disk.SectorsWritten);
			if (_diskBusy.TryGetValue(disk.Name, out var before) && elapsed > 0 && disk.BusyMilliseconds >= before)
			{
				var busy = Clamp((disk.BusyMilliseconds - before) / (elapsed * 10));
				busiest = double.IsNaN(busiest) || busy > busiest ? busy : busiest;
			}

			_diskBusy[disk.Name] = disk.BusyMilliseconds;
		}

		var (read, write) = _diskRate.Complete(elapsed);
		return (read * SectorBytes, write * SectorBytes, busiest);
	}

	/// <summary>Download and upload in bits per second over the counted interfaces; NaN on the first call.</summary>
	private (double Down, double Up) SampleNetwork()
	{
		var timestamp = Stopwatch.GetTimestamp();
		var elapsed = _lastNetworkTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastNetworkTimestamp, timestamp).TotalSeconds;
		_lastNetworkTimestamp = timestamp;
		_networkRate.Begin();
		foreach (var (name, received, sent) in LinuxProc.ParseNetDev(File.ReadAllText("/proc/net/dev")))
		{
			if (_countedInterfaces.TryGetValue(name, out var index))
			{
				_networkRate.Add(index, received, sent);
			}
		}

		var (down, up) = _networkRate.Complete(elapsed);
		return (down * 8, up * 8);
	}

	private double ReadThermal()
	{
		if (_thermal is null)
		{
			return double.NaN;
		}

		var celsius = double.Parse(File.ReadAllText(_thermal).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) / 1000;
		return celsius is <= 0 or >= 125 ? double.NaN : celsius; // Zones that are not wired up report 0 or nonsense.
	}

	/// <summary>Every process of this machine, shared by the Claude split and the process watches; empty on failure.</summary>
	private ReadOnlySpan<ProcessRecord> TakeSnapshot()
	{
		try
		{
			return _snapshot is null ? default : _snapshot.Take();
		}
		catch (Exception ex)
		{
			Log.WarnOnce("collect-process-snapshot", "Taking the process snapshot failed; Claude and the watched processes are shown as unavailable.", ex);
			return default;
		}
	}

	private (IReadOnlyList<ProcessLoad> Cpu, IReadOnlyList<ProcessLoad> Io) SampleTop(ReadOnlySpan<ProcessRecord> records)
	{
		try
		{
			if (_top is not null && _snapshot is not null)
			{
				return _top.Sample(records, _snapshot);
			}
		}
		catch (Exception ex)
		{
			Log.WarnOnce("collect-top", "Reading the busiest processes failed; the panel leaves them out.", ex);
		}

		return ([], []);
	}

	private GitActivity? PollGit(double time, DateTimeOffset wallNow, ReadOnlySpan<ProcessRecord> records)
	{
		try
		{
			return _git?.Poll(time, wallNow, _newEvents, records);
		}
		catch (Exception ex)
		{
			Log.WarnOnce("collect-git", "Following git failed; the git graph is shown as unavailable.", ex);
			return null;
		}
	}

	private ClaudeProcessUsage SampleClaude(ReadOnlySpan<ProcessRecord> records)
	{
		try
		{
			return _claudeProcesses?.Sample(records) ?? ClaudeProcessUsage.None;
		}
		catch (Exception ex)
		{
			Log.WarnOnce("collect-claude-processes", "Reading 'claude-processes' failed; it is shown as unavailable.", ex);
			return ClaudeProcessUsage.None;
		}
	}

	/// <summary>
	/// Pings the default gateway in the background, at most one ping at a time. The round trip of the last answered ping is
	/// reported, NaN when there was no answer (some block ICMP).
	/// </summary>
	private bool Ping()
	{
		if (_pingReply is { IsCompleted: true } finished)
		{
			_pingMs = finished.IsCompletedSuccessfully && finished.Result.Status == IPStatus.Success
				? finished.Result.RoundtripTime
				: double.NaN;
			_pingReply = null;
		}

		if (_pingReply is null && _details?.Network?.Gateway is { } gateway && IPAddress.TryParse(gateway, out var target))
		{
			_pingReply = _ping.SendPingAsync(target, TimeSpan.FromMilliseconds(500), null, null, CancellationToken.None);
		}

		return true;
	}

	private WatchReading[] ReadWatches(ReadOnlySpan<ProcessRecord> records, double memoryTotal)
	{
		if (_watches.Length == 0)
		{
			return [];
		}

		var readings = new WatchReading[_watches.Length];
		readings.AsSpan().Fill(WatchReading.None);
		if (_processWatches is not null)
		{
			try
			{
				_processWatches.Sample(records, memoryTotal, readings);
			}
			catch (Exception ex)
			{
				Log.WarnOnce("collect-process-watch", "Reading the watched processes failed; they are shown as unavailable.", ex);
			}
		}

		return readings;
	}

	/// <summary>Adds the spikes of <paramref name="snapshot"/> to <paramref name="events"/>.</summary>
	private bool CheckSpikes(MetricSnapshot snapshot, DateTimeOffset wallNow, List<PulseEvent> events)
	{
		_spikes.Check(snapshot, wallNow, events);
		return true;
	}

	/// <summary>The marks written since the previous sample, as events.</summary>
	private bool Drain(MarkLog marks, double time, DateTimeOffset wallNow)
	{
		marks.Drain(time, wallNow, _newEvents);
		return true;
	}

	/// <summary>The processes whose memory keeps growing; a new suspect also becomes an event.</summary>
	private IReadOnlyList<LeakSuspect> SampleLeaks(ReadOnlySpan<ProcessRecord> records, double time, DateTimeOffset wallNow)
	{
		if (_leaks is null || _snapshot is null || records.IsEmpty)
		{
			return [];
		}

		try
		{
			return _leaks.Sample(records, _snapshot, time, wallNow, _newEvents);
		}
		catch (Exception ex)
		{
			Log.WarnOnce("collect-leaks", "Looking for growing memory failed; it is left out.", ex);
			return [];
		}
	}

	private static T Guard<T>(string source, Func<T> read, T fallback)
	{
		try
		{
			return read();
		}
		catch (Exception ex)
		{
			Log.WarnOnce($"collect-{source}", $"Reading '{source}' failed; it is shown as unavailable.", ex);
			return fallback;
		}
	}

	private static double Clamp(double percent) => double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : double.NaN;
}
