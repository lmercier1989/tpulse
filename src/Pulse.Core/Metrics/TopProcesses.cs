using System.Diagnostics;

using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>
/// The process names that use the most CPU and the most I/O on this machine right now, grouped by name like the rows of
/// Task Manager ("msedge" adds up all its processes). I/O is what the processes read and write: files, pipes and
/// network together, measured without administrator rights. Names are read from the snapshot once per process.
/// </summary>
internal sealed class TopProcesses
{
	/// <summary>
	/// The number of names in each list: ten, so a moment can be looked into later (what caused that spike); the panel
	/// shows the first few.
	/// </summary>
	public const int Size = 10;

	private readonly ProcessCpuMeter _meter = new();
	private readonly Dictionary<string, Totals> _byName = new(StringComparer.OrdinalIgnoreCase);
	private Dictionary<(int Id, long Created), (string Name, long Io)> _previous = [];
	private Dictionary<(int Id, long Created), (string Name, long Io)> _current = [];
	private long _lastSample;

	/// <summary>The top lists of <paramref name="records"/>, a snapshot taken by <paramref name="snapshot"/>.</summary>
	public (IReadOnlyList<ProcessLoad> Cpu, IReadOnlyList<ProcessLoad> Io) Sample(ReadOnlySpan<ProcessRecord> records, ProcessSnapshot snapshot)
	{
		var timestamp = Stopwatch.GetTimestamp();
		var seconds = _lastSample == 0 ? 0 : Stopwatch.GetElapsedTime(_lastSample, timestamp).TotalSeconds;
		var measured = _meter.Begin();
		_byName.Clear();
		_current.Clear();
		foreach (ref readonly var record in records)
		{
			if (record.Id == 0)
			{
				continue; // The idle process: its "CPU" is the time nothing ran.
			}

			var key = (record.Id, record.CreateTime);
			string name;
			long io = 0;
			if (_previous.TryGetValue(key, out var known))
			{
				name = known.Name;
				io = Math.Max(0, record.IoBytes - known.Io);
			}
			else
			{
				name = Clean(snapshot.NameOf(record));
			}

			_current[key] = (name, record.IoBytes);
			ref var totals = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_byName, name, out _);
			totals.Cpu += _meter.Percent(record);
			totals.Io += io;
			totals.Count++;
		}

		_meter.End();
		(_previous, _current) = (_current, _previous);
		_lastSample = timestamp;
		if (!measured || seconds <= 0)
		{
			return ([], []);
		}

		return (Top(totals => totals.Cpu, 0.05), Top(totals => totals.Io / seconds, 1));
	}

	/// <summary>The <see cref="Size"/> largest of a measure, at least <paramref name="minimum"/>, largest first.</summary>
	internal static List<ProcessLoad> Top(IEnumerable<KeyValuePair<string, (double Value, int Count)>> loads, double minimum)
		=> [.. loads.Where(pair => pair.Value.Value >= minimum)
			.OrderByDescending(pair => pair.Value.Value)
			.ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.Take(Size)
			.Select(pair => new ProcessLoad(pair.Key, pair.Value.Count, pair.Value.Value))];

	/// <summary>"devenv.exe" becomes "devenv"; the System process has no ".exe".</summary>
	internal static string Clean(ReadOnlySpan<char> image)
		=> image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4].ToString() : image.Length == 0 ? "System Idle" : image.ToString();

	private List<ProcessLoad> Top(Func<Totals, double> measure, double minimum)
		=> Top(_byName.Select(pair => new KeyValuePair<string, (double, int)>(pair.Key, (measure(pair.Value), pair.Value.Count))), minimum);

	private struct Totals
	{
		public double Cpu;
		public double Io;
		public int Count;
	}
}
