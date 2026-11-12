using System.Globalization;
using System.Text;

namespace Pulse.Core.Metrics;

/// <summary>
/// A CSV file written with <c>-record</c>, read back for <c>-replay</c>: every row becomes a sample again, on a clock that
/// starts at 0 with the first row, so the recording can be scrolled through like live history. Columns are found by
/// name, so recordings of older versions (with fewer columns) still play. The extra graphs are rebuilt from their
/// columns: a "name_cpu_pct" and "name_memory_mb" pair is a process, any other extra column a counter.
/// </summary>
public sealed class Recording
{
	private const double Megabyte = 1024 * 1024;

	private static readonly HashSet<string> Known =
	[
		"time", "cpu_pct", "claude_cpu_pct", "claude_app_cpu_pct", "claude_sessions_cpu_pct", "claude_tools_cpu_pct",
		"memory_pct", "claude_memory_pct", "claude_memory_mb", "gpu_pct", "vram_pct", "gpu_temp_c", "gpu_power_w", "cpu_temp_c",
		"disk_read_bytes_s", "disk_write_bytes_s", "disk_active_pct", "net_down_bits_s", "net_up_bits_s", "ping_ms", "gateway_ping_ms",
		"tokens_out", "tokens_in", "tokens_cache_write", "tokens_cache_read", "claude_active_sessions", "claude_session_processes",
		"claude_tool_processes", "processes", "threads", "memory_total_mb", "git_changed_lines", "events", "top_cpu", "top_io",
	];

	private Recording(string path, IReadOnlyList<MetricSnapshot> samples, SystemInfo info)
	{
		FilePath = path;
		Samples = samples;
		Info = info;
	}

	public string FilePath { get; }

	/// <summary>The rows as samples, oldest first; <see cref="MetricSnapshot.Time"/> counts seconds from the first row.</summary>
	public IReadOnlyList<MetricSnapshot> Samples { get; }

	/// <summary>What the recording holds: which graphs have data, and its extra graphs.</summary>
	public SystemInfo Info { get; }

	public DateTimeOffset Start => Samples[0].RecordedAt!.Value;

	public DateTimeOffset End => Samples[^1].RecordedAt!.Value;

	/// <summary>Reads a recording; throws <see cref="InvalidDataException"/> with a readable message when it is not one.</summary>
	public static Recording Load(string path)
	{
		using var reader = new StreamReader(path, Encoding.UTF8);
		var header = reader.ReadLine() is { } first ? Split(first) : throw new InvalidDataException($"{path} is empty.");
		var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (var i = 0; i < header.Count; i++)
		{
			index.TryAdd(header[i].Trim(), i);
		}

		if (!index.ContainsKey("time") || !index.ContainsKey("cpu_pct"))
		{
			throw new InvalidDataException($"{path} is not a Pulse recording: it has no time and cpu_pct columns.");
		}

		var (watches, watchColumns) = WatchesOf(header);
		var samples = new List<MetricSnapshot>();
		DateTimeOffset? start = null;
		double previous = double.NaN;
		while (reader.ReadLine() is { } line)
		{
			if (line.Length == 0)
			{
				continue;
			}

			var cells = Split(line);
			if (!DateTimeOffset.TryParse(Cell(cells, index, "time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
			{
				continue;
			}

			start ??= at;
			var time = (at - start.Value).TotalSeconds;
			var perMinute = double.IsNaN(previous) || time <= previous ? double.NaN : 60 / (time - previous);
			previous = time;
			samples.Add(Row(cells, index, at, time, perMinute, watches, watchColumns));
		}

		if (samples.Count == 0)
		{
			throw new InvalidDataException($"{path} has no samples.");
		}

		return new Recording(Path.GetFullPath(path), samples, Describe(samples, watches));
	}

	/// <summary>Splits a CSV line, with quoted cells that may hold commas and doubled quotes.</summary>
	internal static List<string> Split(string line)
	{
		var cells = new List<string>();
		var cell = new StringBuilder();
		var quoted = false;
		for (var i = 0; i < line.Length; i++)
		{
			var c = line[i];
			if (quoted)
			{
				if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
				{
					cell.Append('"');
					i++;
				}
				else if (c == '"')
				{
					quoted = false;
				}
				else
				{
					cell.Append(c);
				}
			}
			else if (c == '"')
			{
				quoted = true;
			}
			else if (c == ',')
			{
				cells.Add(cell.ToString());
				cell.Clear();
			}
			else
			{
				cell.Append(c);
			}
		}

		cells.Add(cell.ToString());
		return cells;
	}

	/// <summary>"commit: Webshop: fix the login || error: Application Error: ..." back into events.</summary>
	internal static List<PulseEvent> EventsOf(string cell, double time, DateTimeOffset at)
	{
		var events = new List<PulseEvent>();
		foreach (var item in cell.Split(CsvRecorder.EventSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = item.Split(": ", 3);
			if (parts.Length < 2)
			{
				continue;
			}

			var kind = PulseEvent.KindOf(parts[0]);
			events.Add(new PulseEvent(time, at, kind, parts[1], parts[0], parts.Length > 2 ? parts[2] : string.Empty));
		}

		return events;
	}

	private static (List<WatchSpec> Watches, List<(int Value, int Memory)> Columns) WatchesOf(List<string> header)
	{
		var watches = new List<WatchSpec>();
		var columns = new List<(int, int)>();
		for (var i = 0; i < header.Count; i++)
		{
			var name = header[i].Trim();
			if (Known.Contains(name) || name.Length == 0)
			{
				continue;
			}

			if (name.EndsWith("_cpu_pct", StringComparison.Ordinal))
			{
				var stem = name[..^"_cpu_pct".Length];
				var memory = header.FindIndex(column => column.Trim() == stem + "_memory_mb");
				watches.Add(new WatchSpec(WatchKind.Process, stem));
				columns.Add((i, memory));
			}
			else if (!name.EndsWith("_memory_mb", StringComparison.Ordinal))
			{
				watches.Add(new WatchSpec(WatchKind.Counter, $@"\Recording\{name}"));
				columns.Add((i, -1));
			}
		}

		return (watches, columns);
	}

	private static MetricSnapshot Row(
		List<string> cells,
		Dictionary<string, int> index,
		DateTimeOffset at,
		double time,
		double perMinute,
		List<WatchSpec> watches,
		List<(int Value, int Memory)> watchColumns)
	{
		double V(string column) => Number(Cell(cells, index, column));
		var memoryTotal = V("memory_total_mb") * Megabyte;
		var memoryPercent = V("memory_pct");
		var memoryUsed = memoryTotal * memoryPercent / 100;
		var tokens = new TokenCounts((long) Zero(V("tokens_in")), (long) Zero(V("tokens_cache_write")), (long) Zero(V("tokens_cache_read")), (long) Zero(V("tokens_out")));
		var hasTokens = !double.IsNaN(V("tokens_out"));
		var readings = new WatchReading[watches.Count];
		for (var i = 0; i < watches.Count; i++)
		{
			var (valueColumn, memoryColumn) = watchColumns[i];
			var value = Number(valueColumn < cells.Count ? cells[valueColumn] : null);
			var memory = memoryColumn >= 0 && memoryColumn < cells.Count ? Number(cells[memoryColumn]) * Megabyte : double.NaN;
			readings[i] = watches[i].Kind == WatchKind.Process
				? new WatchReading(value, memory, memoryTotal > 0 ? memory / memoryTotal * 100 : double.NaN, double.IsNaN(value) ? 0 : 1, double.NaN, double.NaN)
				: WatchReading.OfCounter(value, double.IsNaN(value) ? 0 : 1);
		}

		return new MetricSnapshot
		{
			Time = time,
			RecordedAt = at,
			CpuPercent = V("cpu_pct"),
			ClaudeCpuPercent = V("claude_cpu_pct"),
			MemoryPercent = memoryPercent,
			MemoryTotalBytes = memoryTotal,
			MemoryUsedBytes = memoryUsed,
			MemoryAvailableBytes = memoryTotal - memoryUsed,
			ClaudeMemoryPercent = V("claude_memory_pct"),
			GpuPercent = V("gpu_pct"),
			GpuMemoryPercent = V("vram_pct"),
			GpuTemperatureCelsius = V("gpu_temp_c"),
			GpuPowerWatts = V("gpu_power_w"),
			CpuTemperatureCelsius = V("cpu_temp_c"),
			DiskReadBytesPerSecond = V("disk_read_bytes_s"),
			DiskWriteBytesPerSecond = V("disk_write_bytes_s"),
			DiskActivePercent = V("disk_active_pct"),
			NetworkDownBitsPerSecond = V("net_down_bits_s"),
			NetworkUpBitsPerSecond = V("net_up_bits_s"),
			PingMs = index.ContainsKey("ping_ms") ? V("ping_ms") : V("gateway_ping_ms"),
			ClaudeTokens = tokens,
			ClaudeOutputPerMinute = hasTokens ? tokens.Output * perMinute : double.NaN,
			ClaudeInputPerMinute = hasTokens ? tokens.FreshInput * perMinute : double.NaN,
			ProcessCount = V("processes"),
			ThreadCount = V("threads"),
			GitChangedLines = V("git_changed_lines"),
			Watches = readings,
			Events = EventsOf(Cell(cells, index, "events") ?? string.Empty, time, at),
			TopCpu = CsvRecorder.ParseLoads(Cell(cells, index, "top_cpu")),
			TopIo = CsvRecorder.ParseLoads(Cell(cells, index, "top_io")),
		};
	}

	/// <summary>Which graphs the recording has data for; the others show as unavailable.</summary>
	private static SystemInfo Describe(List<MetricSnapshot> samples, List<WatchSpec> watches)
	{
		bool Has(Func<MetricSnapshot, double> value) => samples.Any(sample => double.IsFinite(value(sample)));
		var gpu = Has(sample => sample.GpuPercent);
		return new SystemInfo
		{
			CpuName = "recorded CPU",
			LogicalProcessors = 0,
			HasCpu = Has(sample => sample.CpuPercent),
			GpuName = gpu ? "recorded GPU" : null,
			HasGpu = gpu,
			HasMemory = Has(sample => sample.MemoryPercent),
			HasDisk = Has(sample => sample.DiskReadBytesPerSecond),
			HasNetwork = Has(sample => sample.NetworkDownBitsPerSecond),
			NetworkAdapters = "recorded",
			HasClaude = Has(sample => sample.ClaudeOutputPerMinute) || Has(sample => sample.ClaudeCpuPercent),
			Watches = watches,
			FollowsGit = Has(sample => sample.GitChangedLines),
			GitRepositories = Has(sample => sample.GitChangedLines) ? ["recorded"] : [],
		};
	}

	private static string? Cell(List<string> cells, Dictionary<string, int> index, string column)
		=> index.TryGetValue(column, out var i) && i < cells.Count ? cells[i] : null;

	private static double Number(string? text)
		=> double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

	private static double Zero(double value) => double.IsFinite(value) ? value : 0;
}
