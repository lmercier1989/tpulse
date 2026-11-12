using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Pulse.Core.Formatting;

namespace Pulse.Core.Metrics;

/// <summary>One command measured with <c>tpulse -- command</c>, as the run history keeps it.</summary>
/// <param name="At">When it started.</param>
/// <param name="Folder">The folder it ran in.</param>
/// <param name="Command">The command line.</param>
/// <param name="Seconds">How long it took.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Commit">The commit checked out where it ran; null outside a git repository.</param>
/// <param name="CpuAverage">Average CPU of the machine while it ran, in %; NaN when it ran too briefly to say.</param>
/// <param name="MemoryPeakBytes">The most memory in use while it ran; NaN when it ran too briefly to say.</param>
/// <param name="ReadBytes">Bytes read from disk while it ran.</param>
/// <param name="WrittenBytes">Bytes written to disk while it ran.</param>
public sealed record RunRecord(
	DateTimeOffset At,
	string Folder,
	string Command,
	double Seconds,
	int ExitCode,
	string? Commit,
	double CpuAverage,
	double MemoryPeakBytes,
	double ReadBytes,
	double WrittenBytes)
{
	/// <summary>The first seven characters of the commit, as git shows it; empty without one.</summary>
	[JsonIgnore]
	public string ShortCommit => Commit is { Length: >= 7 } commit ? commit[..7] : Commit ?? string.Empty;

	/// <summary>Whether <paramref name="other"/> is the same command, run in the same folder.</summary>
	public bool SameAs(RunRecord other)
		=> string.Equals(Command, other.Command, StringComparison.Ordinal)
		   && string.Equals(Folder, other.Folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>
/// The run history (%LOCALAPPDATA%\Pulse\runs.jsonl): one JSON line per command measured with <c>tpulse -- command</c>,
/// so the next run of the same command in the same folder is compared with the ones before it ("24% slower"), and
/// <c>tpulse -runs</c> lists them.
/// </summary>
public static class RunHistory
{
	/// <summary>Past this size the oldest half of the runs is dropped, so the file never grows without bound.</summary>
	internal const long MaximumLength = 1024 * 1024;

	/// <summary>The earlier runs a run is compared with at most: the latest ones, so an old, faster machine fades out.</summary>
	internal const int MaximumCompared = 10;

	/// <summary>Within this share of the average, a run reads as "about as fast".</summary>
	private const double SamePercent = 5;

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
	};

	/// <summary>%LOCALAPPDATA%\Pulse\runs.jsonl (on Linux ~/.local/share/Pulse/runs.jsonl).</summary>
	public static string DefaultPath()
		=> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "runs.jsonl");

	/// <summary>The commit checked out in <paramref name="folder"/>'s repository; null outside one.</summary>
	public static string? CommitIn(string folder) => GitRepository.TryOpen(folder)?.ReadHeadCommit();

	/// <summary>Adds <paramref name="run"/>; past <see cref="MaximumLength"/> the older half of the runs goes first.</summary>
	public static void Append(string path, RunRecord run)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		if (File.Exists(path) && new FileInfo(path).Length > MaximumLength)
		{
			var lines = File.ReadAllLines(path);
			File.WriteAllLines(path, lines[(lines.Length / 2)..]);
		}

		using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
		stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(run, Json) + "\n"));
	}

	/// <summary>Every run kept, oldest first; a line that cannot be read is left out. Empty when there is no history yet.</summary>
	public static List<RunRecord> Load(string path)
	{
		var runs = new List<RunRecord>();
		if (!File.Exists(path))
		{
			return runs;
		}

		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream, Encoding.UTF8);
		while (reader.ReadLine() is { } line)
		{
			try
			{
				if (line.Length > 0 && JsonSerializer.Deserialize<RunRecord>(line, Json) is { Command: not null, Folder: not null } run)
				{
					runs.Add(run);
				}
			}
			catch (JsonException)
			{
				// A line cut off by a full disk, or written by a later version: the others still count.
			}
		}

		return runs;
	}

	/// <summary>
	/// "  Before    avg 58 s over 5 runs (fastest 52 s, last 55 s at a3f9c1e) · this run 24% slower": <paramref name="run"/>
	/// against the earlier runs of the same command in the same folder that succeeded; null when there are none.
	/// </summary>
	public static string? Compare(IReadOnlyList<RunRecord> earlier, RunRecord run)
	{
		var before = earlier.Where(other => other.ExitCode == 0 && other.Seconds > 0 && other.SameAs(run)).TakeLast(MaximumCompared).ToList();
		if (before.Count == 0)
		{
			return null;
		}

		var culture = CultureInfo.CurrentCulture;
		var average = before.Average(other => other.Seconds);
		var last = before[^1];
		var lastCommit = last.Commit is not null && last.Commit != run.Commit ? $" at {last.ShortCommit}" : string.Empty;
		var detail = before.Count == 1
			? $"{Units.Duration(last.Seconds, culture)} the run before{lastCommit}"
			: $"avg {Units.Duration(average, culture)} over {before.Count} runs (fastest {Units.Duration(before.Min(other => other.Seconds), culture)}, last {Units.Duration(last.Seconds, culture)}{lastCommit})";
		var change = (run.Seconds - average) / average * 100;
		var verdict = Math.Abs(change) < SamePercent ? "about as fast"
			: change > 0 ? $"{change.ToString("0", culture)}% slower"
			: $"{(-change).ToString("0", culture)}% faster";
		return $"  Before    {detail} · this run {verdict}";
	}

	/// <summary>
	/// "tpulse: 3 runs of dotnet build: avg 1 min 2 s, fastest 58 s, slowest 1 min 8 s": what <c>-repeat</c> adds after
	/// the last run.
	/// </summary>
	public static string Repeated(string command, IReadOnlyList<double> seconds)
	{
		var culture = CultureInfo.CurrentCulture;
		return $"tpulse: {seconds.Count} runs of {command}: avg {Units.Duration(seconds.Average(), culture)}, fastest {Units.Duration(seconds.Min(), culture)}, slowest {Units.Duration(seconds.Max(), culture)}";
	}

	/// <summary>
	/// The rows of <c>tpulse -runs</c>, latest first: when, how long, the exit code, the commit and the command, with the
	/// folder in front when <paramref name="folder"/> is null (every folder).
	/// </summary>
	public static List<string> List(IReadOnlyList<RunRecord> runs, string? folder, int count)
	{
		var culture = CultureInfo.CurrentCulture;
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var shown = runs.Where(run => folder is null || string.Equals(run.Folder, folder, comparison)).Reverse().Take(count).ToList();
		var lines = new List<string>();
		foreach (var run in shown)
		{
			var where = folder is null ? $"{Path.GetFileName(run.Folder.TrimEnd('\\', '/')),-16} " : string.Empty;
			lines.Add($"  {run.At.ToLocalTime().ToString("g", culture),-17} {Units.Duration(run.Seconds, culture),10}  exit {run.ExitCode,-3} {run.ShortCommit,-7}  {where}{run.Command}");
		}

		return lines;
	}
}
