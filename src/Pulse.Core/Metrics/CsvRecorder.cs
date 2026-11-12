using System.Globalization;
using System.Text;

namespace Pulse.Core.Metrics;

/// <summary>
/// Writes every sample to a CSV file, so the hardware and Claude can be compared afterwards (Excel, pandas, Power BI):
/// one row per sample, the wall-clock time first, numbers in invariant culture, an empty cell for a reading that is
/// not available. Token columns hold the tokens of the answers written during that sample, so they add up over rows.
/// The extra graphs of <c>-process</c> and <c>-counter</c> get columns of their own at the end.
/// </summary>
public sealed class CsvRecorder : IDisposable
{
	private static readonly string[] Columns =
	[
		"time",
		"cpu_pct", "claude_cpu_pct", "claude_app_cpu_pct", "claude_sessions_cpu_pct", "claude_tools_cpu_pct",
		"memory_pct", "claude_memory_pct", "claude_memory_mb",
		"gpu_pct", "vram_pct", "gpu_temp_c", "gpu_power_w", "cpu_temp_c",
		"disk_read_bytes_s", "disk_write_bytes_s", "disk_active_pct",
		"net_down_bits_s", "net_up_bits_s", "ping_ms",
		"tokens_out", "tokens_in", "tokens_cache_write", "tokens_cache_read",
		"claude_active_sessions", "claude_session_processes", "claude_tool_processes",
		"processes", "threads",
		"memory_total_mb", "git_changed_lines", "events", "top_cpu", "top_io",
	];

	/// <summary>Events in one cell: "commit: Webshop: fix the login || error: Application Error: ...".</summary>
	internal const string EventSeparator = " || ";

	private readonly StreamWriter _writer;
	private readonly StringBuilder _line = new(512);
	private readonly IReadOnlyList<WatchSpec> _watches;

	/// <param name="path">The file to write; an existing file is replaced.</param>
	/// <param name="watches">The extra graphs of this launch, written after the fixed columns.</param>
	public CsvRecorder(string path, IReadOnlyList<WatchSpec>? watches = null)
	{
		_watches = watches ?? [];
		FilePath = Path.GetFullPath(path);
		var directory = Path.GetDirectoryName(FilePath);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		_writer = new StreamWriter(FilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		_writer.WriteLine(string.Join(',', [.. Columns, .. WatchColumns(_watches)]));
		_writer.Flush();
	}

	public string FilePath { get; }

	/// <summary>
	/// A new file in Documents\Pulse named after the current time, for example pulse-20261111-143005.csv, with the name
	/// of the other machine when one is read (pulse-server01-20261111-143005.csv).
	/// </summary>
	public static string DefaultPath(string? host = null)
	{
		var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		var stamp = TimeProvider.System.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
		var machine = string.IsNullOrWhiteSpace(host) ? string.Empty : Slug(host) + "-";
		return Path.Combine(documents, "Pulse", $"pulse-{machine}{stamp}.csv");
	}

	/// <summary>
	/// The columns of the extra graphs: "shop_api_cpu_pct" and "shop_api_memory_mb" for a process, the
	/// counter name ("current_connections") for a counter; a name used twice gets "_2".
	/// </summary>
	internal static IEnumerable<string> WatchColumns(IReadOnlyList<WatchSpec> watches)
	{
		var used = new HashSet<string>(StringComparer.Ordinal);
		foreach (var watch in watches)
		{
			var name = Slug(watch.Title);
			for (var n = 2; !used.Add(name); n++)
			{
				name = $"{Slug(watch.Title)}_{n}";
			}

			if (watch.Kind == WatchKind.Process)
			{
				yield return name + "_cpu_pct";
				yield return name + "_memory_mb";
			}
			else
			{
				yield return name;
			}
		}
	}

	/// <summary>Writes <paramref name="sample"/> as taken now.</summary>
	public void Write(MetricSnapshot sample) => Write(sample, TimeProvider.System.GetLocalNow());

	/// <summary>Writes <paramref name="sample"/> as taken at <paramref name="at"/>: a sample saved later, such as a selection.</summary>
	public void Write(MetricSnapshot sample, DateTimeOffset at)
	{
		var claude = sample.ClaudeProcesses;
		var tokens = sample.ClaudeTokens;
		_line.Clear().Append(at.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
		Add(sample.CpuPercent).Add(sample.ClaudeCpuPercent).Add(claude.AppCpu).Add(claude.SessionsCpu).Add(claude.ToolsCpu);
		Add(sample.MemoryPercent).Add(sample.ClaudeMemoryPercent).Add(claude.TotalBytes / (1024 * 1024));
		Add(sample.GpuPercent).Add(sample.GpuMemoryPercent).Add(sample.GpuTemperatureCelsius).Add(sample.GpuPowerWatts).Add(sample.CpuTemperatureCelsius);
		Add(sample.DiskReadBytesPerSecond).Add(sample.DiskWriteBytesPerSecond).Add(sample.DiskActivePercent);
		Add(sample.NetworkDownBitsPerSecond).Add(sample.NetworkUpBitsPerSecond).Add(sample.PingMs);
		Add(tokens.Output).Add(tokens.Input).Add(tokens.CacheWrite).Add(tokens.CacheRead);
		Add(sample.ClaudeSessions.Count).Add(claude.Sessions).Add(claude.Tools);
		Add(sample.ProcessCount).Add(sample.ThreadCount);
		Add(sample.MemoryTotalBytes / (1024 * 1024)).Add(sample.GitChangedLines);
		AddText(string.Join(EventSeparator, sample.Events.Select(item => $"{item.Action}: {item.Source}: {item.Text}")));
		AddText(Loads(sample.TopCpu));
		AddText(Loads(sample.TopIo));
		for (var i = 0; i < _watches.Count; i++)
		{
			var reading = i < sample.Watches.Count ? sample.Watches[i] : WatchReading.None;
			Add(reading.Value);
			if (_watches[i].Kind == WatchKind.Process)
			{
				Add(reading.MemoryBytes / (1024 * 1024));
			}
		}

		_writer.WriteLine(_line);
		_writer.Flush();
	}

	public void Dispose() => _writer.Dispose();

	/// <summary>"Shop.Api" becomes "shop_api", "% Processor Time" becomes "processor_time".</summary>
	private static string Slug(string text)
	{
		var slug = new StringBuilder(text.Length);
		foreach (var c in text.ToLowerInvariant())
		{
			if (char.IsAsciiLetterOrDigit(c))
			{
				slug.Append(c);
			}
			else if (slug.Length > 0 && slug[^1] != '_')
			{
				slug.Append('_');
			}
		}

		return slug.ToString().TrimEnd('_') is { Length: > 0 } result ? result : "value";
	}

	/// <summary>The busiest processes in one cell: "dotnet:70.4:7|node:5.7:15" (name, value, processes of that name).</summary>
	internal static string Loads(IReadOnlyList<ProcessLoad> loads)
		=> string.Join('|', loads.Select(load => $"{load.Name}:{Math.Round(load.Value, 3).ToString("0.###", CultureInfo.InvariantCulture)}:{load.Count}"));

	/// <summary>A cell written by <see cref="Loads"/>, read back.</summary>
	internal static List<ProcessLoad> ParseLoads(string? cell)
	{
		var loads = new List<ProcessLoad>();
		foreach (var item in (cell ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = item.Split(':');
			if (parts.Length == 3
				&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
				&& int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
			{
				loads.Add(new ProcessLoad(parts[0], count, value));
			}
		}

		return loads;
	}

	/// <summary>A text cell, quoted when it holds a comma, a quote or a line break, as Excel and pandas expect.</summary>
	private void AddText(string text)
	{
		_line.Append(',');
		if (text.AsSpan().IndexOfAny(",\"\r\n") < 0)
		{
			_line.Append(text);
			return;
		}

		_line.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal).ReplaceLineEndings(" ")).Append('"');
	}

	private CsvRecorder Add(double value)
	{
		_line.Append(',');
		if (double.IsFinite(value))
		{
			_line.Append(Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture));
		}

		return this;
	}
}
