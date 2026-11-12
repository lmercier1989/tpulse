using System.Globalization;

using Pulse.Core.Metrics;
using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.CommandLine;

/// <summary>How the terminal monitor draws its graphs.</summary>
public enum TerminalRenderer
{
	/// <summary>Sixel when the terminal reports support for it, braille otherwise.</summary>
	Auto,
	Sixel,
	Braille,
}

/// <summary>
/// Command-line options: <c>tpulse -cpu -memory -history 5m</c>. Options start with <c>-</c>, <c>--</c> or
/// <c>/</c> (a bare graph name works too), a value follows as the next argument or after <c>=</c> or <c>:</c>.
/// Options apply to one launch only; they are never written to the settings file.
/// </summary>
public sealed class LaunchOptions
{
	public const string HelpText =
		"""
        Pulse - a minimal system monitor

        Usage: tpulse [graphs] [options]

        Graphs (default: the graphs that were shown last time)
          -cpu              CPU utilization, per core and temperature where available
          -gpu              GPU utilization, dedicated memory, temperature and power
          -memory, -mem     Physical memory in use
          -disk             Disk read and write throughput
          -network, -net    Network download and upload
          -claude           Claude tokens per minute (and Claude's share in the CPU and memory graphs)
          -git [folder]     Git: lines changed and not committed yet; commits, checkouts, merges and
                            rebases become markers on the time axis. Without a folder Pulse finds the
                            repositories on this machine itself; with one it follows the repository there,
                            or every repository directly inside it (-git D:\Repos)
          -all              All graphs

        Extra graphs (keys 8, 9 and 0)
          -process <name>   CPU and memory of every process with that name, or of one process id
          -counter <path>   Any performance counter by its English path, for example
                            "\Web Service(_Total)\Current Connections" (typeperf -q lists them)
                            Up to three -process and -counter graphs together.

        Another machine
          -host <name>      Read the counters of another Windows machine over the network. It needs the
                            Remote Registry service there, and your account in its Performance Monitor Users
                            group (or an administrator there). GPU and Claude are read on this machine only.

        Measure and mark
          -- <command>      Run a command in this shell and measure it: a mark at its start and end on every
                            running Pulse, and afterwards what it cost (CPU, memory, disk, network and the
                            busiest processes). For example: tpulse -- dotnet build
          -mark <text>      Put a mark on every running Pulse, for example: tpulse -mark deploy started

        Profiles and recordings
          -profile <name>   The options saved under that name (samples: claude, dev, server, web, sql)
          -save-profile <name> [options]   Save the options that follow under that name
          -profiles         List the profiles (in %LOCALAPPDATA%\Pulse\profiles.json)
          -record [file]    Write every sample to CSV (default: Documents\Pulse\pulse-<time>.csv)
          -replay <file>    Play a recording back; scroll through it with the arrow keys

        Options
          -history <time>   History shown: 30s, 60s or 5m (10s to 10m)
          -interval <time>  Sampling interval: 0.5s, 1s or 2s (250ms to 5s)
          -fps <n>          Highest frame rate, 15 to 120 (terminal default 30, window 60)
          -notify           Windows notifications when a load stays high, a disk fills up or git is blocked
          -cores            Show the per-core CPU traces
          -compact          Compact layout
          -sixel            Draw with Sixel pixel graphics (default when the terminal supports it)
          -braille          Draw with braille characters (works in every terminal)
          -here             Draw in this tab; in Windows Terminal, tpulse otherwise opens its own Pulse tab
          -window           Open a separate window instead of drawing in the terminal
          -top              Keep that window on top
          -help             Show this help
          -version          Show the version

        Examples
          tpulse -cpu -memory -history 5m
          tpulse -cpu -process devenv -process Shop.Api
          tpulse -host sql01 -cpu -memory -disk -process sqlservr
          tpulse -git D:\Repos -claude -cpu
          tpulse -profile dev -record

        Options given here apply to this launch only; changes made with the keys are remembered.
        In the monitor, press ? for the keys and mouse, ? again for these commands, and q to quit.
        """;

	private readonly HashSet<MetricKind> _metrics = [];
	private readonly List<WatchSpec> _watches = [];
	private readonly List<string> _gitPaths = [];

	/// <summary>The built-in graphs named on the command line; null when none were named.</summary>
	public IReadOnlySet<MetricKind>? Metrics => _metrics.Count == 0 ? null : _metrics;

	/// <summary>The extra graphs of <c>-process</c> and <c>-counter</c>, in the order given.</summary>
	public IReadOnlyList<WatchSpec> Watches => _watches;

	/// <summary>The other machine to read with <c>-host</c>; null for this machine.</summary>
	public string? Host { get; private set; }

	/// <summary>The folders given with <c>-git</c>; empty when Pulse finds the repositories itself.</summary>
	public IReadOnlyList<string> GitPaths => _gitPaths;

	/// <summary>Show Windows notifications for alerts.</summary>
	public bool Notify { get; private set; }

	/// <summary>The recording to play back with <c>-replay</c>; null when live.</summary>
	public string? ReplayPath { get; private set; }

	public double? HistorySeconds { get; private set; }

	public double? IntervalSeconds { get; private set; }

	public int? Fps { get; private set; }

	public bool Cores { get; private set; }

	public bool Compact { get; private set; }

	public bool Top { get; private set; }

	/// <summary>Open the graphical window instead of drawing in the terminal.</summary>
	public bool Window { get; private set; }

	/// <summary>Internal: this process is the detached window started by <c>-window</c>.</summary>
	public bool WindowHost { get; private set; }

	/// <summary>Draw in the current terminal tab instead of opening a Pulse tab in Windows Terminal.</summary>
	public bool Here { get; private set; }

	/// <summary>Write every sample to a CSV file: the path given, or an empty string for a new file in Documents\Pulse.</summary>
	public string? RecordPath { get; private set; }

	public TerminalRenderer Renderer { get; private set; }

	public bool ShowHelp { get; private set; }

	public bool ShowVersion { get; private set; }

	/// <summary>
	/// Whether the command line picks the graphs: a built-in graph or an extra graph was named. Then only those are
	/// shown; otherwise the graphs of last time.
	/// </summary>
	public bool SelectsGraphs => _metrics.Count > 0 || _watches.Count > 0;

	/// <summary>Whether the options change which graphs are shown or how tall they are.</summary>
	public bool AffectsLayout => SelectsGraphs || Compact;

	public static bool TryParse(IReadOnlyList<string> args, out LaunchOptions options, out string? error)
	{
		options = new LaunchOptions();
		error = null;

		for (var i = 0; i < args.Count; i++)
		{
			var raw = args[i];
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}

			var token = raw.Trim().TrimStart('-', '/');
			string? inlineValue = null;
			var separator = token.IndexOfAny(['=', ':']);
			if (separator > 0)
			{
				inlineValue = token[(separator + 1)..];
				token = token[..separator];
			}

			switch (token.ToLowerInvariant())
			{
				case "cpu":
					options._metrics.Add(MetricKind.Cpu);
					break;
				case "gpu":
					options._metrics.Add(MetricKind.Gpu);
					break;
				case "memory" or "mem" or "ram":
					options._metrics.Add(MetricKind.Memory);
					break;
				case "disk" or "disks":
					options._metrics.Add(MetricKind.Disk);
					break;
				case "network" or "net":
					options._metrics.Add(MetricKind.Network);
					break;
				case "claude" or "tokens":
					options._metrics.Add(MetricKind.Claude);
					break;
				case "all":
					options._metrics.UnionWith(MetricKinds.Builtin);
					break;
				case "process" or "proc":
					if (!TryTakeValue(args, ref i, inlineValue, out var process)
						|| process.IndexOfAny(['\\', '/', '*', '?']) >= 0)
					{
						error = $"Option '{raw}' needs a process name or id, for example -process devenv or -process 4120.";
						return false;
					}

					options._watches.Add(new WatchSpec(WatchKind.Process, process.Trim()));
					break;
				case "counter" or "ctr":
					if (!TryTakeValue(args, ref i, inlineValue, out var counter) || !CounterPath.TryParse(counter, out _))
					{
						error = $"Option '{raw}' needs a counter path, for example -counter \"\\Web Service(_Total)\\Current Connections\".";
						return false;
					}

					options._watches.Add(new WatchSpec(WatchKind.Counter, counter.Trim().Trim('"')));
					break;
				case "git" or "repo" or "repos":
					// The git graph, and optionally the folders to follow: the next argument is one only when it is a folder,
					// so "-git -cpu" and "-git cpu" work. Without a folder Pulse finds the repositories itself.
					options._metrics.Add(MetricKind.Git);
					if (!string.IsNullOrWhiteSpace(inlineValue))
					{
						options._gitPaths.Add(inlineValue.Trim().Trim('"'));
					}
					else if (i + 1 < args.Count && !args[i + 1].StartsWith('-') && Directory.Exists(args[i + 1].Trim('"')))
					{
						options._gitPaths.Add(args[++i].Trim('"'));
					}

					break;
				case "notify" or "notifications":
					options.Notify = true;
					break;
				case "replay" or "play":
					if (!TryTakeValue(args, ref i, inlineValue, out var replay))
					{
						error = $"Option '{raw}' needs a recording, for example -replay Documents\\Pulse\\pulse-20261111-143005.csv.";
						return false;
					}

					options.ReplayPath = replay.Trim('"');
					break;
				case "host" or "computer" or "machine":
					var host = TryTakeValue(args, ref i, inlineValue, out var hostValue) ? hostValue.Trim().TrimStart('\\') : string.Empty;
					if (host.Length == 0 || host.Length > 253 || host.IndexOfAny([' ', '\\', '/', '"']) >= 0)
					{
						error = $"Option '{raw}' needs a machine name or address, for example -host server01.";
						return false;
					}

					options.Host = host;
					break;
				case "history" or "hist":
					if (!TryTakeValue(args, ref i, inlineValue, out var history)
						|| !DurationParser.TryParseSeconds(history, out var historySeconds)
						|| historySeconds < PulseSettings.MinHistorySeconds
						|| historySeconds > PulseSettings.MaxHistorySeconds)
					{
						error = $"Option '{raw}' needs a history from 10s to 10m, for example 30s, 60s or 5m.";
						return false;
					}

					options.HistorySeconds = historySeconds;
					break;
				case "interval" or "refresh":
					if (!TryTakeValue(args, ref i, inlineValue, out var interval)
						|| !DurationParser.TryParseSeconds(interval, out var intervalSeconds)
						|| intervalSeconds < PulseSettings.MinIntervalSeconds
						|| intervalSeconds > PulseSettings.MaxIntervalSeconds)
					{
						error = $"Option '{raw}' needs an interval from 250ms to 5s, for example 0.5s, 1s or 2s.";
						return false;
					}

					options.IntervalSeconds = intervalSeconds;
					break;
				case "fps":
					if (!TryTakeValue(args, ref i, inlineValue, out var fpsText)
						|| !int.TryParse(fpsText, NumberStyles.None, CultureInfo.InvariantCulture, out var fps)
						|| fps < PulseSettings.MinFps
						|| fps > PulseSettings.MaxFps)
					{
						error = $"Option '{raw}' needs a frame rate from {PulseSettings.MinFps} to {PulseSettings.MaxFps}.";
						return false;
					}

					options.Fps = fps;
					break;
				case "cores" or "percore":
					options.Cores = true;
					break;
				case "compact" or "mini":
					options.Compact = true;
					break;
				case "top" or "ontop" or "topmost":
					options.Top = true;
					break;
				case "window" or "gui":
					options.Window = true;
					break;
				case "window-host":
					options.WindowHost = true;
					break;
				case "here" or "inline":
					options.Here = true;
					break;
				case "record" or "rec":
					options.RecordPath = TryTakeValue(args, ref i, inlineValue, out var recordPath) ? recordPath : string.Empty;
					break;
				case "sixel":
					options.Renderer = TerminalRenderer.Sixel;
					break;
				case "braille" or "text":
					options.Renderer = TerminalRenderer.Braille;
					break;
				case "help" or "h" or "?":
					options.ShowHelp = true;
					break;
				case "version" or "v":
					options.ShowVersion = true;
					break;
				default:
					error = $"Unknown option '{raw}'.";
					return false;
			}
		}

		if (options._watches.Count > WatchSpec.MaximumCount)
		{
			error = $"Up to {WatchSpec.MaximumCount} -process and -counter graphs together fit on the keys.";
			return false;
		}

		if (options.Host is not null && options._watches.FirstOrDefault(watch => watch.ProcessId is not null) is { } byId)
		{
			error = $"With -host, name the process instead of its id ({byId.Target}): process ids are read on this machine only.";
			return false;
		}

		return true;
	}

	/// <summary>Applies the options to the settings of this launch.</summary>
	public void ApplyTo(PulseSettings settings)
	{
		if (SelectsGraphs)
		{
			foreach (var kind in MetricKinds.Builtin)
			{
				settings.Channel(kind).Visible = _metrics.Contains(kind);
			}
		}

		if (HistorySeconds is { } history)
		{
			settings.HistorySeconds = history;
		}

		if (IntervalSeconds is { } interval)
		{
			settings.IntervalSeconds = interval;
		}

		if (Fps is { } fps)
		{
			settings.TargetFps = fps;
		}

		settings.ShowCores |= Cores;
		settings.Compact |= Compact;
		settings.AlwaysOnTop |= Top;
		settings.Notify |= Notify;
	}

	private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, string? inlineValue, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
	{
		if (!string.IsNullOrWhiteSpace(inlineValue))
		{
			value = inlineValue;
			return true;
		}

		if (index + 1 < args.Count && !args[index + 1].StartsWith('-'))
		{
			value = args[++index];
			return true;
		}

		value = null;
		return false;
	}
}
