using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Microsoft.Win32;

using Pulse.Core.Diagnostics;
using Pulse.Core.Formatting;
using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>
/// Reads every metric from its Windows source. Each source is probed once at start-up; a source that is
/// missing or fails is logged and reported as NaN, so one broken sensor never stops the others.
/// With a host, the same performance counters are read from that machine over the network; what Windows only offers
/// locally (the GPU interfaces, Claude) is left out there. Not thread-safe: <see cref="Sampler"/> calls
/// <see cref="Collect"/> from one loop only.
/// </summary>
public sealed class SystemMetricsCollector : IDisposable
{
	// Task Manager's CPU figure: utility accounts for frequency scaling, unlike % Processor Time.
	private const string CpuUtilityPath = @"\Processor Information(_Total)\% Processor Utility";
	private const string CpuTimePath = @"\Processor(_Total)\% Processor Time";
	private const string CoreUtilityPath = @"\Processor Information(*)\% Processor Utility";
	private const string CoreTimePath = @"\Processor(*)\% Processor Time";
	private const string DiskReadPath = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
	private const string DiskWritePath = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
	private const string DiskIdlePath = @"\PhysicalDisk(*)\% Idle Time";
	private const string GpuEnginePath = @"\GPU Engine(*)\Utilization Percentage";
	private const string GpuDedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
	private const string ThermalTenthsKelvinPath = @"\Thermal Zone Information(*)\High Precision Temperature";
	private const string ThermalKelvinPath = @"\Thermal Zone Information(*)\Temperature";
	private const string ProcessesPath = @"\System\Processes";
	private const string ThreadsPath = @"\System\Threads";

	// Read from another machine only: there the local APIs (GlobalMemoryStatusEx, GetIfTable2, DriveInfo) do not reach.
	private const string MemoryTotalPath = @"\NUMA Node Memory(_Total)\Total MBytes";
	private const string MemoryAvailablePath = @"\NUMA Node Memory(_Total)\Available MBytes";
	private const string NetworkReceivedPath = @"\Network Interface(*)\Bytes Received/sec";
	private const string NetworkSentPath = @"\Network Interface(*)\Bytes Sent/sec";
	private const string NetworkBandwidthPath = @"\Network Interface(*)\Current Bandwidth";
	private const string VolumeFreePercentPath = @"\LogicalDisk(*)\% Free Space";
	private const string VolumeFreeMegabytesPath = @"\LogicalDisk(*)\Free Megabytes";
	private const string UptimePath = @"\System\System Up Time";
	private const string ProcessCpuPath = @"\Process(*)\% Processor Time";
	private const string ProcessMemoryPath = @"\Process(*)\Working Set - Private";
	private const string ProcessThreadsPath = @"\Process(*)\Thread Count";
	private const string ProcessHandlesPath = @"\Process(*)\Handle Count";
	private const string ProcessIoPath = @"\Process(*)\IO Data Bytes/sec";

	private const double KelvinOffset = 273.15;
	private const double DetailsRefreshSeconds = 5;
	private const double Megabyte = 1024 * 1024;

	private readonly string? _host;
	private readonly Lock _gate = new();
	private WatchSpec[] _watches;
	private nint[] _watchCounters;
	private readonly PdhQuery? _pdh;
	private readonly nint _cpu;
	private readonly nint _cores;
	private readonly nint _diskRead;
	private readonly nint _diskWrite;
	private readonly nint _diskIdle;
	private readonly nint _gpuEngine;
	private readonly nint _gpuMemory;
	private readonly nint _thermal;
	private readonly nint _processes;
	private readonly nint _threads;
	private readonly nint _memoryTotal;
	private readonly nint _memoryAvailable;
	private readonly nint _networkReceived;
	private readonly nint _networkSent;
	private readonly nint _networkBandwidth;
	private readonly nint _volumeFreePercent;
	private readonly nint _volumeFreeMegabytes;
	private readonly nint _uptime;
	private readonly nint _processCpu;
	private readonly nint _processMemory;
	private readonly nint _processThreads;
	private readonly nint _processHandles;
	private readonly nint _processIo;
	private readonly GitMonitor? _git;
	private readonly WindowsEventSource? _windowsEvents;
	private readonly TopProcesses? _top;
	private readonly LeakDetector? _leaks;
	private readonly ListeningPorts? _ports;
	private PortActivity? _portActivity;
	private double _portsReadAt = double.NegativeInfinity;
	private readonly SpikeDetector _spikes = new();
	private readonly MarkLog _marks = new(MarkLog.DefaultPath());
	private readonly List<PulseEvent> _newEvents = [];
	private readonly bool _thermalInTenths;
	private readonly GpuAdapter? _gpu;
	private readonly D3dkmtAdapter? _gpuKernel;
	private readonly NvmlGpu? _nvml;
	private readonly NetworkCounters _network = new();
	private readonly GpuEngineAggregator _engines = new();
	private readonly Dictionary<int, int> _coreSlots = [];
	private readonly ProcessSnapshot? _snapshot;
	private readonly ClaudeProcesses? _claudeProcesses;
	private ProcessWatches? _processWatches;
	private readonly ClaudeTranscripts? _claudeTranscripts;
	private readonly IPAddress? _hostAddress;
	private readonly string _remoteOperatingSystem = string.Empty;
	private readonly Ping _ping = new();
	private Task<PingReply>? _pingReply;
	private double _pingMs = double.NaN;
	private SystemDetails? _details;
	private double _detailsReadAt = double.NegativeInfinity;
	private double _lastCollect = double.NaN;
	private bool _disposed;

	private SystemMetricsCollector(string? host, IReadOnlyList<WatchSpec> watches, IReadOnlyList<string>? gitPaths)
	{
		_host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
		_watches = [.. watches];
		_watchCounters = new nint[_watches.Length];
		var remote = _host is not null;

		_pdh = PdhQuery.TryOpen();
		if (_pdh is not null)
		{
			_cpu = Add(CpuUtilityPath);
			if (_cpu == 0)
			{
				_cpu = Add(CpuTimePath);
			}

			_cores = Add(CoreUtilityPath);
			if (_cores == 0)
			{
				_cores = Add(CoreTimePath);
			}

			_diskRead = Add(DiskReadPath);
			_diskWrite = Add(DiskWritePath);
			_diskIdle = Add(DiskIdlePath);

			_thermal = Add(ThermalTenthsKelvinPath);
			_thermalInTenths = _thermal != 0;
			if (_thermal == 0)
			{
				_thermal = Add(ThermalKelvinPath);
			}

			_processes = Add(ProcessesPath);
			_threads = Add(ThreadsPath);

			for (var i = 0; i < _watches.Length; i++)
			{
				if (_watches[i].Counter is { } counter)
				{
					_watchCounters[i] = _pdh.TryAddCounter(counter.ToPath(_host));
				}
			}

			if (remote)
			{
				_memoryTotal = Add(MemoryTotalPath);
				_memoryAvailable = Add(MemoryAvailablePath);
				_networkReceived = Add(NetworkReceivedPath);
				_networkSent = Add(NetworkSentPath);
				_networkBandwidth = Add(NetworkBandwidthPath);
				_volumeFreePercent = Add(VolumeFreePercentPath);
				_volumeFreeMegabytes = Add(VolumeFreeMegabytesPath);
				_uptime = Add(UptimePath);
				// CPU and I/O per process for the top lists of the panel; the rest only for -process.
				_processCpu = Add(ProcessCpuPath);
				_processIo = Add(ProcessIoPath);
				if (_watches.Any(watch => watch.Kind == WatchKind.Process))
				{
					_processMemory = Add(ProcessMemoryPath);
					_processThreads = Add(ProcessThreadsPath);
					_processHandles = Add(ProcessHandlesPath);
				}
			}
		}

		string? adapters;
		string cpuName;
		int processors;
		if (remote)
		{
			// The GPU interfaces (DXGI, D3DKMT, NVML) and Claude exist only on this machine.
			_pdh?.Collect(); // Instant counters (bandwidth, sizes) have values after one collect; it primes the rates too.
			adapters = Guard("remote-adapters", ReadRemoteAdapters, null);
			(cpuName, processors) = Guard("remote-cpu", () => ReadRemoteCpu(_host!), (HardwareNames.CleanCpuName(string.Empty), 0));
			_remoteOperatingSystem = Guard("remote-os", () => ReadRemoteOperatingSystem(_host!), string.Empty);
			_hostAddress = Guard("remote-address", () => ResolveHost(_host!), null);
			Log.Info($"Reading {_host} ({_hostAddress?.ToString() ?? "address unknown"}): {cpuName}, {processors} logical processors, {_remoteOperatingSystem}.");
		}
		else
		{
			_gpu = SelectGpu();
			if (_gpu is { } gpu)
			{
				if (_pdh is not null)
				{
					_gpuEngine = _pdh.TryAddCounter(GpuEnginePath);
					_gpuMemory = _pdh.TryAddCounter(GpuDedicatedPath);
				}

				_gpuKernel = D3dkmtAdapter.TryOpen(gpu.LuidLow, gpu.LuidHigh);
				Log.Info(_gpuKernel is null
					? "D3DKMT adapter could not be opened; GPU temperature falls back to NVML where available."
					: "D3DKMT adapter opened for GPU temperature.");
				_nvml = NvmlGpu.TryOpen(gpu.VendorId, gpu.DeviceId);
			}

			if (ProcessSnapshot.IsSupported)
			{
				_snapshot = new ProcessSnapshot { WatchedNames = ProcessWatches.ImageNames(_watches) };
				_claudeProcesses = new ClaudeProcesses();
				if (_watches.Any(watch => watch.Kind == WatchKind.Process))
				{
					_processWatches = new ProcessWatches(_watches);
				}
			}

			_claudeTranscripts = Guard("claude-transcripts", ClaudeTranscripts.TryCreate, null);
			_windowsEvents = Guard("windows-events", WindowsEventSource.TryCreate, null);
			if (ProcessSnapshot.IsSupported)
			{
				_top = new TopProcesses();
				_leaks = new LeakDetector();
				_ports = new ListeningPorts();
			}

			var counted = Guard("network-describe", NetworkCounters.DescribeCountedInterfaces, []);
			adapters = counted.Count == 0 ? null : string.Join(" + ", counted);
			cpuName = ReadCpuName();
			processors = Environment.ProcessorCount;
		}

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
			LogicalProcessors = processors,
			HasCpu = _cpu != 0,
			HasCpuCores = _cores != 0,
			HasCpuTemperature = _thermal != 0,
			GpuName = _gpu?.Name,
			GpuDedicatedBytes = _gpu is { DedicatedVideoMemory: > 0 } g ? g.DedicatedVideoMemory : double.NaN,
			HasGpu = _gpu is not null && _gpuEngine != 0,
			HasMemory = !remote || _memoryTotal != 0,
			HasDisk = _diskRead != 0 || _diskWrite != 0,
			NetworkAdapters = adapters,
			HasNetwork = !remote || _networkReceived != 0,
			HasClaude = _claudeProcesses is not null || _claudeTranscripts is not null,
			Host = _host,
			Watches = _watches,
			FollowsGit = _git is not null,
			GitRepositories = [.. _git?.Repositories.Select(repository => repository.Name) ?? []],
			HasWindowsEvents = _windowsEvents is not null,
		};

		nint Add(string path) => _pdh!.TryAddCounter(On(path));
	}

	public SystemInfo Info { get; }

	/// <summary>This machine, without extra graphs.</summary>
	public static SystemMetricsCollector Create() => new(null, [], null);

	/// <summary><paramref name="host"/> (null for this machine), with the extra graphs of <paramref name="watches"/>.</summary>
	public static SystemMetricsCollector Create(string? host, IReadOnlyList<WatchSpec> watches) => new(host, watches, null);

	/// <summary>
	/// <paramref name="host"/> (null for this machine), with the extra graphs of <paramref name="watches"/>, following git:
	/// in the repositories of <paramref name="gitPaths"/> (a folder in a repository, or a folder of repositories), in every
	/// repository Pulse finds itself when the list is empty, or not at all when it is null.
	/// </summary>
	public static SystemMetricsCollector Create(string? host, IReadOnlyList<WatchSpec> watches, IReadOnlyList<string>? gitPaths) => new(host, watches, gitPaths);

	/// <summary>
	/// Checks before start-up that <paramref name="host"/> lets Pulse read its counters and that every counter of
	/// <paramref name="watches"/> exists there; returns what is wrong, in words for the person who typed the command,
	/// or null when everything can be read. Connecting to a machine that does not answer can take a while.
	/// </summary>
	/// <summary>The names of the git repositories <c>-git</c> finds for <paramref name="path"/>.</summary>
	public static IReadOnlyList<string> GitRepositoriesAt(string path) => [.. GitRepository.Find(path).Select(repository => repository.Name)];

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
			var status = Native.PdhConnectMachine(host);
			if (status != Native.ErrorSuccess)
			{
				return DescribeConnectError(host, status);
			}
		}

		if (!watches.Any(watch => watch.Kind == WatchKind.Counter))
		{
			return null;
		}

		using var pdh = PdhQuery.TryOpen();
		if (pdh is null)
		{
			return "Performance counters cannot be read on this machine.";
		}

		foreach (var watch in watches)
		{
			if (watch.Counter is not { } counter)
			{
				continue;
			}

			var path = counter.ToPath(host);
			var status = pdh.AddCounter(path, out _);
			if (status != Native.ErrorSuccess)
			{
				return $"The counter {path} does not exist{(counter.Machine ?? host) switch { null => string.Empty, var machine => $" on {machine}" }} (0x{status:X8}). "
					   + "Use the English name, as typeperf -q lists it, for example \"\\Processor(_Total)\\% Processor Time\".";
			}
		}

		return null;
	}

	/// <summary>
	/// Follows the processes named <paramref name="name"/> from the next sample on, as one more extra graph: only on this
	/// machine, and up to <see cref="WatchSpec.MaximumCount"/> extra graphs. <paramref name="index"/> is where its
	/// readings stand in <see cref="MetricSnapshot.Watches"/>.
	/// </summary>
	public bool TryFollowProcess(string name, out WatchSpec watch, out int index)
	{
		watch = new WatchSpec(WatchKind.Process, name);
		index = -1;
		lock (_gate)
		{
			if (_host is not null || _snapshot is null || _watches.Length >= WatchSpec.MaximumCount
				|| _watches.Any(existing => existing.Kind == WatchKind.Process && string.Equals(existing.Target, name, StringComparison.OrdinalIgnoreCase)))
			{
				return false;
			}

			_watches = [.. _watches, watch];
			_watchCounters = [.. _watchCounters, 0];
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

		_pdh?.Collect();
		var time = MonotonicClock.Now;

		var (memoryTotal, memoryAvailable) = _host is null
			? Guard("memory", ReadMemory, (double.NaN, double.NaN))
			: Guard("remote-memory", ReadRemoteMemory, (double.NaN, double.NaN));
		var memoryUsed = memoryTotal - memoryAvailable;
		var (down, up) = _host is null
			? Guard("network", _network.Sample, (double.NaN, double.NaN))
			: Guard("remote-network", ReadRemoteNetwork, (double.NaN, double.NaN));
		var gpuMemoryUsed = Guard("gpu-memory", ReadGpuMemory, double.NaN);
		if (time - _detailsReadAt >= DetailsRefreshSeconds)
		{
			_details = _host is null ? SystemDetailsReader.Read() : Guard("remote-details", ReadRemoteDetails, null);
			_detailsReadAt = time;
			Guard("ping", Ping, false);
		}

		var cores = Guard("cpu-cores", ReadCores, []);
		var records = TakeSnapshot();
		var claude = SampleClaude(records);
		var watches = ReadWatches(records, memoryTotal, cores.Length);
		var (topCpu, topIo) = SampleTop(records, cores.Length);
		_newEvents.Clear();
		var wallNow = TimeProvider.System.GetUtcNow();
		var git = PollGit(time, wallNow, records);
		_windowsEvents?.Drain(time, wallNow, _newEvents);
		Guard("marks", () => Drain(_marks, time, wallNow), false);
		var leaks = SampleLeaks(records, time, wallNow);
		var ports = SamplePorts(records, time);
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
			CpuPercent = Clamp(_pdh?.ReadValue(_cpu) ?? double.NaN),
			CpuCorePercents = cores,
			CpuTemperatureCelsius = Guard("thermal", ReadThermal, double.NaN),
			GpuPercent = Guard("gpu-engine", ReadGpuUtilization, double.NaN),
			GpuMemoryUsedBytes = gpuMemoryUsed,
			GpuMemoryPercent = Info.GpuDedicatedBytes > 0 ? Clamp(gpuMemoryUsed / Info.GpuDedicatedBytes * 100) : double.NaN,
			GpuTemperatureCelsius = Guard("gpu-temperature", ReadGpuTemperature, double.NaN),
			GpuPowerWatts = Guard("gpu-power", () => _nvml?.ReadPowerWatts() ?? double.NaN, double.NaN),
			MemoryTotalBytes = memoryTotal,
			MemoryAvailableBytes = memoryAvailable,
			MemoryUsedBytes = memoryUsed,
			MemoryPercent = memoryTotal > 0 ? Clamp(memoryUsed / memoryTotal * 100) : double.NaN,
			DiskReadBytesPerSecond = NonNegative(_pdh?.ReadValue(_diskRead) ?? double.NaN),
			DiskWriteBytesPerSecond = NonNegative(_pdh?.ReadValue(_diskWrite) ?? double.NaN),
			DiskActivePercent = Guard("disk-active", ReadBusiestDisk, double.NaN),
			NetworkDownBitsPerSecond = down,
			NetworkUpBitsPerSecond = up,
			ProcessCount = _pdh?.ReadValue(_processes) ?? double.NaN,
			ThreadCount = _pdh?.ReadValue(_threads) ?? double.NaN,
			UptimeSeconds = _host is null ? Environment.TickCount64 / 1000.0 : _pdh?.ReadValue(_uptime) ?? double.NaN,
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
			Ports = ports,
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
		_windowsEvents?.Dispose();
		_nvml?.Dispose();
		_gpuKernel?.Dispose();
		_pdh?.Dispose();
	}

	/// <summary>"server01 cannot be reached ...": what a failed connection means and what to do about it.</summary>
	internal static string DescribeConnectError(string host, uint status) => status switch
	{
		0x800007D0 or 53 or 67 or 1722 => $"{host} cannot be reached for its performance counters. Check the name, and that the "
										  + $"Remote Registry service runs on {host} (it is off by default on Windows 10 and 11).",
		5 or 0xC0000BDB => $"{host} refused access to its performance counters. Add your account to the Performance Monitor Users "
						   + $"group on {host}, or run tpulse as an account that is an administrator there.",
		_ => $"The performance counters of {host} cannot be read (0x{status:X8}).",
	};

	private static GpuAdapter? SelectGpu()
	{
		var adapters = Guard("dxgi", DxgiAdapters.Enumerate, []);
		foreach (var adapter in adapters)
		{
			Log.Info($"GPU adapter: {adapter.Name} (vendor 0x{adapter.VendorId:X4}, device 0x{adapter.DeviceId:X4}, LUID 0x{adapter.Luid:X16}, {Units.Bytes(adapter.DedicatedVideoMemory, CultureInfo.InvariantCulture)} dedicated)");
		}

		// The adapter with the most dedicated memory is the discrete GPU on hybrid systems.
		GpuAdapter? selected = null;
		foreach (var adapter in adapters)
		{
			if (selected is null || adapter.DedicatedVideoMemory > selected.Value.DedicatedVideoMemory)
			{
				selected = adapter;
			}
		}

		Log.Info(selected is { } chosen ? $"GPU selected: {chosen.Name}" : "No hardware GPU adapter found.");
		return selected;
	}

	private static string ReadCpuName()
	{
		try
		{
			using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
			return HardwareNames.CleanCpuName(key?.GetValue("ProcessorNameString") as string ?? string.Empty);
		}
		catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			Log.Warn("Could not read the processor name.", ex);
			return HardwareNames.CleanCpuName(string.Empty);
		}
	}

	/// <summary>
	/// The processor name and the number of logical processors of another machine, from its registry. That part of the
	/// registry is often closed to accounts that are not an administrator there; the per-core counters then give the count.
	/// </summary>
	private static (string Name, int Processors) ReadRemoteCpu(string host)
	{
		using var machine = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, host);
		using var processors = machine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
		using var first = processors?.OpenSubKey("0");
		return (HardwareNames.CleanCpuName(first?.GetValue("ProcessorNameString") as string ?? string.Empty), processors?.SubKeyCount ?? 0);
	}

	/// <summary>"Windows Server 2022 Standard (20348.2700)", from a key every account may read remotely.</summary>
	private static string ReadRemoteOperatingSystem(string host)
	{
		using var machine = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, host);
		using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
		return key is null
			? string.Empty
			: SystemDetailsReader.DescribeWindows(
				key.GetValue("ProductName") as string,
				key.GetValue("DisplayVersion") as string,
				key.GetValue("CurrentBuild") as string,
				key.GetValue("UBR") is int revision ? revision : null);
	}

	private static IPAddress? ResolveHost(string host)
	{
		var addresses = Dns.GetHostAddresses(host);
		return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
	}

	private static (double Total, double Available) ReadMemory()
	{
		var status = new MemoryStatusEx { Length = (uint) Unsafe.SizeOf<MemoryStatusEx>() };
		return Native.GlobalMemoryStatusEx(ref status) ? (status.TotalPhys, status.AvailPhys) : (double.NaN, double.NaN);
	}

	/// <summary>The path of a counter on the monitored machine.</summary>
	private string On(string path) => _host is null ? path : $@"\\{_host}{path}";

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

	/// <summary>The top lists of the panel: from the snapshot on this machine, from the Process counters of another one.</summary>
	private (IReadOnlyList<ProcessLoad> Cpu, IReadOnlyList<ProcessLoad> Io) SampleTop(ReadOnlySpan<ProcessRecord> records, int cores)
	{
		try
		{
			if (_top is not null && _snapshot is not null)
			{
				return _top.Sample(records, _snapshot);
			}

			if (_host is not null && _processCpu != 0)
			{
				return ReadRemoteTop(Info.LogicalProcessors > 0 ? Info.LogicalProcessors : cores);
			}
		}
		catch (Exception ex)
		{
			Log.WarnOnce("collect-top", "Reading the busiest processes failed; the panel leaves them out.", ex);
		}

		return ([], []);
	}

	/// <summary>
	/// The busiest process names of another machine. Instances of one name ("w3wp", "w3wp#1") are added up; "_Total" and
	/// "Idle" are not processes. % Processor Time counts one core as 100, so it is divided by the logical processors.
	/// </summary>
	private (IReadOnlyList<ProcessLoad> Cpu, IReadOnlyList<ProcessLoad> Io) ReadRemoteTop(int processors)
	{
		var cpu = Group(_processCpu, processors > 0 ? processors : 1);
		var io = Group(_processIo, 1);
		return (TopProcesses.Top(cpu, 0.05), TopProcesses.Top(io, 1));

		Dictionary<string, (double Value, int Count)> Group(nint counter, double divisor)
		{
			var groups = new Dictionary<string, (double Value, int Count)>(StringComparer.OrdinalIgnoreCase);
			foreach (ref readonly var item in _pdh!.ReadArray(counter))
			{
				var name = PdhQuery.NameOf(item);
				var hash = name.IndexOf('#');
				if (hash >= 0)
				{
					name = name[..hash];
				}

				if (!PdhQuery.IsValid(item.Value.CStatus) || name.SequenceEqual("_Total") || name.SequenceEqual("Idle"))
				{
					continue;
				}

				ref var group = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(groups, name.ToString(), out _);
				group = (group.Value + (Math.Max(0, item.Value.DoubleValue) / divisor), group.Count + 1);
			}

			return groups;
		}
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
	/// Pings in the background, at most one ping at a time: the default gateway, or the other machine with a host. The
	/// round trip of the last answered ping is reported, NaN when there was no answer (some block ICMP).
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

		var target = _host is not null
			? _hostAddress
			: _details?.Network?.Gateway is { } gateway && IPAddress.TryParse(gateway, out var address) ? address : null;
		if (_pingReply is null && target is not null)
		{
			_pingReply = _ping.SendPingAsync(target, TimeSpan.FromMilliseconds(500), null, null, CancellationToken.None);
		}

		return true;
	}

	private WatchReading[] ReadWatches(ReadOnlySpan<ProcessRecord> records, double memoryTotal, int cores)
	{
		if (_watches.Length == 0)
		{
			return [];
		}

		var readings = new WatchReading[_watches.Length];
		readings.AsSpan().Fill(WatchReading.None);
		for (var i = 0; i < _watches.Length; i++)
		{
			if (_watches[i].Counter is { } counter)
			{
				readings[i] = Guard("counter-watch", () => ReadCounterWatch(_watchCounters[i], counter), WatchReading.None);
			}
		}

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
		else if (_host is not null && _processMemory != 0)
		{
			var processors = Info.LogicalProcessors > 0 ? Info.LogicalProcessors : cores;
			Guard("remote-process-watch", () => ReadRemoteProcessWatches(readings, memoryTotal, processors), false);
		}

		return readings;
	}

	/// <summary>One counter, or the sum of its instances when the path has a wildcard (without "_Total").</summary>
	private WatchReading ReadCounterWatch(nint counter, CounterPath path)
	{
		if (_pdh is null || counter == 0)
		{
			return WatchReading.None;
		}

		if (!path.IsWildcard)
		{
			var value = _pdh.ReadValue(counter);
			return WatchReading.OfCounter(value, double.IsNaN(value) ? 0 : 1);
		}

		var sum = 0.0;
		var instances = 0;
		foreach (ref readonly var item in _pdh.ReadArray(counter))
		{
			if (PdhQuery.IsValid(item.Value.CStatus) && !PdhQuery.NameOf(item).Equals("_Total", StringComparison.OrdinalIgnoreCase))
			{
				sum += item.Value.DoubleValue;
				instances++;
			}
		}

		return WatchReading.OfCounter(instances == 0 ? double.NaN : sum, instances);
	}

	/// <summary>
	/// The watched processes of another machine, from its "Process" counters. Their instances are named after the image
	/// without ".exe", with "#1", "#2" for the second and third process of that name. % Processor Time counts one core as
	/// 100, so it is divided by the logical processors to get a share of the machine like on this machine.
	/// </summary>
	private bool ReadRemoteProcessWatches(WatchReading[] readings, double memoryTotal, int processors)
	{
		var cpu = new double[_watches.Length];
		var bytes = new double[_watches.Length];
		var threads = new double[_watches.Length];
		var handles = new double[_watches.Length];
		var counts = new int[_watches.Length];
		Sum(_processCpu, cpu, counts);
		Sum(_processMemory, bytes, null);
		Sum(_processThreads, threads, null);
		Sum(_processHandles, handles, null);
		for (var i = 0; i < _watches.Length; i++)
		{
			if (_watches[i].Kind == WatchKind.Process)
			{
				readings[i] = counts[i] == 0
					? WatchReading.None
					: new WatchReading(
						processors > 0 ? Clamp(cpu[i] / processors) : double.NaN,
						bytes[i],
						memoryTotal > 0 ? bytes[i] / memoryTotal * 100 : double.NaN,
						counts[i],
						threads[i],
						handles[i]);
			}
		}

		return true;

		void Sum(nint counter, double[] totals, int[]? found)
		{
			foreach (ref readonly var item in _pdh!.ReadArray(counter))
			{
				if (!PdhQuery.IsValid(item.Value.CStatus))
				{
					continue;
				}

				var name = PdhQuery.NameOf(item);
				var hash = name.IndexOf('#');
				if (hash >= 0)
				{
					name = name[..hash];
				}

				for (var i = 0; i < _watches.Length; i++)
				{
					if (_watches[i].ProcessName.Length > 0 && name.Equals(_watches[i].ProcessName, StringComparison.OrdinalIgnoreCase))
					{
						totals[i] += Math.Max(0, item.Value.DoubleValue);
						if (found is not null)
						{
							found[i]++;
						}
					}
				}
			}
		}
	}

	private (double Total, double Available) ReadRemoteMemory()
	{
		var total = _pdh?.ReadValue(_memoryTotal) ?? double.NaN;
		var available = _pdh?.ReadValue(_memoryAvailable) ?? double.NaN;
		return (total * Megabyte, available * Megabyte);
	}

	/// <summary>Download and upload in bits per second over the physical adapters of another machine.</summary>
	private (double Down, double Up) ReadRemoteNetwork()
	{
		return (Sum(_networkReceived) * 8, Sum(_networkSent) * 8);

		double Sum(nint counter)
		{
			var sum = double.NaN;
			foreach (ref readonly var item in _pdh!.ReadArray(counter))
			{
				if (PdhQuery.IsValid(item.Value.CStatus) && NetworkInterfaceNames.IsCounted(PdhQuery.NameOf(item)))
				{
					sum = (double.IsNaN(sum) ? 0 : sum) + Math.Max(0, item.Value.DoubleValue);
				}
			}

			return sum;
		}
	}

	private string? ReadRemoteAdapters()
	{
		var names = new List<string>();
		foreach (ref readonly var item in _pdh!.ReadArray(_networkBandwidth))
		{
			var name = PdhQuery.NameOf(item);
			if (NetworkInterfaceNames.IsCounted(name))
			{
				names.Add(new string(name));
			}
		}

		return names.Count == 0 ? null : string.Join(" + ", names);
	}

	/// <summary>The info panel of another machine: its volumes from the LogicalDisk counters, its address and adapters.</summary>
	private SystemDetails ReadRemoteDetails()
	{
		var free = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
		foreach (ref readonly var item in _pdh!.ReadArray(_volumeFreeMegabytes))
		{
			var name = PdhQuery.NameOf(item);
			if (PdhQuery.IsValid(item.Value.CStatus) && IsDriveLetter(name))
			{
				free[new string(name)] = item.Value.DoubleValue * Megabyte;
			}
		}

		var disks = new List<DiskSpace>();
		foreach (ref readonly var item in _pdh.ReadArray(_volumeFreePercent))
		{
			var name = new string(PdhQuery.NameOf(item));
			if (PdhQuery.IsValid(item.Value.CStatus) && free.TryGetValue(name, out var freeBytes) && item.Value.DoubleValue > 0)
			{
				disks.Add(new DiskSpace(name, string.Empty, freeBytes / (item.Value.DoubleValue / 100), freeBytes));
			}
		}

		disks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

		var adapters = new List<string>();
		double link = 0;
		foreach (ref readonly var item in _pdh.ReadArray(_networkBandwidth))
		{
			var name = PdhQuery.NameOf(item);
			if (PdhQuery.IsValid(item.Value.CStatus) && NetworkInterfaceNames.IsCounted(name))
			{
				adapters.Add(new string(name));
				link += item.Value.DoubleValue;
			}
		}

		var network = new NetworkDetails(
			adapters.Count == 0 ? "unknown" : string.Join(" + ", adapters),
			_hostAddress?.AddressFamily == AddressFamily.InterNetwork ? _hostAddress.ToString() : null,
			null,
			null,
			_hostAddress?.AddressFamily == AddressFamily.InterNetworkV6 ? _hostAddress.ToString() : null,
			(long) link);
		return new SystemDetails(_host!, _remoteOperatingSystem, disks, network, null, Remote: true);

		static bool IsDriveLetter(ReadOnlySpan<char> name) => name.Length == 2 && char.IsAsciiLetter(name[0]) && name[1] == ':';
	}

	private double[] ReadCores()
	{
		if (_pdh is null || _cores == 0)
		{
			return [];
		}

		var items = _pdh.ReadArray(_cores);
		if (items.IsEmpty)
		{
			return [];
		}

		if (_coreSlots.Count == 0)
		{
			AssignCoreSlots(items);
		}

		var values = new double[_coreSlots.Count];
		values.AsSpan().Fill(double.NaN);
		foreach (ref readonly var item in items)
		{
			if (PdhQuery.IsValid(item.Value.CStatus)
				&& ProcessorInstanceName.TryParse(PdhQuery.NameOf(item), out var key)
				&& _coreSlots.TryGetValue(key, out var slot))
			{
				values[slot] = Clamp(item.Value.DoubleValue);
			}
		}

		return values;
	}

	private void AssignCoreSlots(ReadOnlySpan<PdhFmtCounterValueItem> items)
	{
		var keys = new List<int>();
		foreach (ref readonly var item in items)
		{
			if (ProcessorInstanceName.TryParse(PdhQuery.NameOf(item), out var key))
			{
				keys.Add(key);
			}
		}

		keys.Sort();
		for (var slot = 0; slot < keys.Count; slot++)
		{
			_coreSlots[keys[slot]] = slot;
		}
	}

	private double ReadThermal()
	{
		if (_pdh is null || _thermal == 0)
		{
			return double.NaN;
		}

		var hottest = double.NaN;
		var cpuZone = double.NaN;
		foreach (ref readonly var item in _pdh.ReadArray(_thermal))
		{
			if (!PdhQuery.IsValid(item.Value.CStatus))
			{
				continue;
			}

			var celsius = (_thermalInTenths ? item.Value.DoubleValue / 10 : item.Value.DoubleValue) - KelvinOffset;
			if (celsius is <= 0 or >= 125)
			{
				continue; // ACPI zones that are not wired up report 0 K or nonsense.
			}

			hottest = MaxIgnoringNaN(hottest, celsius);
			if (PdhQuery.NameOf(item).Contains("CPU", StringComparison.OrdinalIgnoreCase))
			{
				cpuZone = MaxIgnoringNaN(cpuZone, celsius);
			}
		}

		return double.IsNaN(cpuZone) ? hottest : cpuZone;
	}

	private double ReadBusiestDisk()
	{
		if (_pdh is null || _diskIdle == 0)
		{
			return double.NaN;
		}

		var busiest = double.NaN;
		foreach (ref readonly var item in _pdh.ReadArray(_diskIdle))
		{
			if (PdhQuery.IsValid(item.Value.CStatus) && !PdhQuery.NameOf(item).Contains("_Total", StringComparison.Ordinal))
			{
				busiest = MaxIgnoringNaN(busiest, Clamp(100 - item.Value.DoubleValue));
			}
		}

		return busiest;
	}

	private double ReadGpuUtilization()
	{
		if (_pdh is null || _gpuEngine == 0 || _gpu is not { } gpu)
		{
			return double.NaN;
		}

		_engines.Reset();
		foreach (ref readonly var item in _pdh.ReadArray(_gpuEngine))
		{
			if (PdhQuery.IsValid(item.Value.CStatus)
				&& GpuInstanceName.TryParseEngine(PdhQuery.NameOf(item), out var luid, out var physical, out var engine)
				&& luid == gpu.Luid)
			{
				_engines.Add(physical, engine, item.Value.DoubleValue);
			}
		}

		return _engines.Busiest;
	}

	private double ReadGpuMemory()
	{
		if (_pdh is null || _gpuMemory == 0 || _gpu is not { } gpu)
		{
			return double.NaN;
		}

		var used = double.NaN;
		foreach (ref readonly var item in _pdh.ReadArray(_gpuMemory))
		{
			if (PdhQuery.IsValid(item.Value.CStatus)
				&& GpuInstanceName.TryParseAdapter(PdhQuery.NameOf(item), out var luid, out _)
				&& luid == gpu.Luid)
			{
				used = (double.IsNaN(used) ? 0 : used) + Math.Max(0, item.Value.DoubleValue);
			}
		}

		return used;
	}

	private double ReadGpuTemperature()
	{
		if (_gpuKernel is not null && _gpuKernel.TryQuery(out var perf) && perf.Temperature > 0)
		{
			return perf.Temperature / 10.0;
		}

		return _nvml?.ReadTemperatureCelsius() ?? double.NaN;
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

	/// <summary>The ports programs listen on, read again every few seconds like the other slow facts; null on another machine.</summary>
	private PortActivity? SamplePorts(ReadOnlySpan<ProcessRecord> records, double time)
	{
		if (_ports is null || _snapshot is null || records.IsEmpty || time - _portsReadAt < DetailsRefreshSeconds)
		{
			return _portActivity;
		}

		_portsReadAt = time;
		try
		{
			_portActivity = _ports.Sample(records, _snapshot);
		}
		catch (Exception ex)
		{
			Log.WarnOnce("collect-ports", "Reading the listening ports failed; the panel leaves them out.", ex);
		}

		return _portActivity;
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

	private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : double.NaN;

	private static double MaxIgnoringNaN(double current, double candidate)
		=> double.IsNaN(current) || candidate > current ? candidate : current;
}
