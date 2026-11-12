using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>
/// The processes named with <c>-process</c> on this machine. Per watch, every process with that image name (or the one
/// process with that id) is added up: CPU in % of the machine, private memory, threads and handles, like the grouped
/// rows of Task Manager.
/// </summary>
internal sealed class ProcessWatches
{
	private readonly IReadOnlyList<WatchSpec> _watches;
	private readonly ProcessCpuMeter _meter = new();
	private readonly Totals[] _totals;

	/// <param name="watches">Every watch of the launch; counters among them are left alone.</param>
	public ProcessWatches(IReadOnlyList<WatchSpec> watches)
	{
		_watches = watches;
		_totals = new Totals[watches.Count];
	}

	/// <summary>
	/// The image names a <see cref="ProcessSnapshot"/> should mark, by watch index ("" for none): "devenv.exe" on Windows,
	/// the program name as it is ("code") on Linux.
	/// </summary>
	public static string[] ImageNames(IReadOnlyList<WatchSpec> watches)
		=> [.. watches.Select(watch => watch.ProcessName.Length == 0 ? string.Empty : OperatingSystem.IsWindows() ? watch.ProcessName + ".exe" : watch.ProcessName)];

	/// <summary>Fills the reading of every process watch from <paramref name="records"/>, a snapshot of all processes.</summary>
	public void Sample(ReadOnlySpan<ProcessRecord> records, double memoryTotalBytes, Span<WatchReading> readings)
	{
		var measured = _meter.Begin();
		Array.Clear(_totals);
		foreach (ref readonly var record in records)
		{
			if (record.Watch >= 0)
			{
				Add(record.Watch, record);
			}

			for (var watch = 0; watch < _watches.Count; watch++)
			{
				if (_watches[watch].ProcessId == record.Id && record.Watch != watch)
				{
					Add(watch, record);
				}
			}
		}

		_meter.End();
		for (var watch = 0; watch < _watches.Count; watch++)
		{
			if (_watches[watch].Kind != WatchKind.Process)
			{
				continue;
			}

			ref readonly var total = ref _totals[watch];
			readings[watch] = total.Count == 0
				? WatchReading.None
				: new WatchReading(
					measured ? total.Cpu : double.NaN,
					total.Bytes,
					memoryTotalBytes > 0 ? total.Bytes / memoryTotalBytes * 100 : double.NaN,
					total.Count,
					total.Threads,
					total.Handles);
		}

		void Add(int watch, in ProcessRecord record)
		{
			ref var total = ref _totals[watch];
			total.Cpu += _meter.Percent(record);
			total.Bytes += record.PrivateBytes;
			total.Threads += record.Threads;
			total.Handles += record.Handles;
			total.Count++;
		}
	}

	private struct Totals
	{
		public double Cpu;
		public double Bytes;
		public double Threads;
		public double Handles;
		public int Count;
	}
}
