using System.Globalization;

using Pulse.Core.Formatting;
using Pulse.Core.Metrics;
using Pulse.Core.Processing;

namespace Pulse.Core.Presentation;

/// <summary>
/// What a command measured with <c>tpulse -- command</c> cost the machine, from the samples taken while it ran: CPU,
/// memory, the bytes read, written, received and sent, and the processes that carried the load.
/// </summary>
public sealed class RunSummary
{
	/// <summary>A longer gap between two samples is not counted, so a sleeping machine adds no traffic.</summary>
	private const double MaximumStepSeconds = 5;

	private readonly List<MetricSnapshot> _samples = [];
	private double _read;
	private double _written;
	private double _received;
	private double _sent;

	public int Count => _samples.Count;

	public void Add(MetricSnapshot sample)
	{
		if (_samples.Count > 0 && sample.Time - _samples[^1].Time is var step and > 0 and <= MaximumStepSeconds)
		{
			_read += Amount(sample.DiskReadBytesPerSecond, step);
			_written += Amount(sample.DiskWriteBytesPerSecond, step);
			_received += Amount(sample.NetworkDownBitsPerSecond, step) / 8;
			_sent += Amount(sample.NetworkUpBitsPerSecond, step) / 8;
		}

		_samples.Add(sample);
	}

	/// <summary>
	/// "tpulse: dotnet build took 1 min 12 s, exit code 0", then a row each for CPU, memory, disk, network and the busiest
	/// processes; just the first row when there were too few samples to say more.
	/// </summary>
	public List<string> Lines(string command, double seconds, int exitCode)
	{
		var culture = CultureInfo.CurrentCulture;
		var lines = new List<string> { $"tpulse: {command} took {Units.Duration(seconds, culture)}, exit code {exitCode}" };
		var measured = _samples.Skip(1).ToList(); // The first sample has no rates yet.
		if (measured.Count == 0)
		{
			return lines;
		}

		var cpu = RangeStats.Of([.. measured.Select(sample => sample.CpuPercent).Where(double.IsFinite)]);
		if (!cpu.IsEmpty)
		{
			lines.Add($"  CPU       avg {Units.Percent(cpu.Average, culture)}, peak {Units.Percent(cpu.Max, culture)}");
		}

		var memory = measured.Where(sample => double.IsFinite(sample.MemoryUsedBytes)).ToList();
		if (memory.Count > 0)
		{
			var peak = memory.MaxBy(sample => sample.MemoryUsedBytes)!;
			lines.Add($"  Memory    peak {Units.Bytes(peak.MemoryUsedBytes, culture)} ({Units.Percent(peak.MemoryPercent, culture)}), at the start {Units.Bytes(memory[0].MemoryUsedBytes, culture)}");
		}

		lines.Add($"  Disk      read {Units.Bytes(_read, culture)}, written {Units.Bytes(_written, culture)}");
		lines.Add($"  Network   received {Units.Bytes(_received, culture)}, sent {Units.Bytes(_sent, culture)}");
		var busiest = ProcessCausation.AverageCpu(measured, 5);
		if (busiest.Count > 0)
		{
			lines.Add($"  Busiest   {string.Join(", ", busiest.Select(load => $"{Name(load)} {InfoPanel.Cpu(load)}"))}");
		}

		var io = ProcessCausation.AverageIo(measured, 3);
		if (io.Count > 0)
		{
			var span = measured[^1].Time - _samples[0].Time;
			lines.Add($"  I/O       {string.Join(", ", io.Select(load => $"{Name(load)} {Units.Bytes(load.Value * span, culture)}"))}");
		}

		return lines;
	}

	/// <summary>This run as the run history keeps it: how long, the exit code, the commit, CPU, memory and disk.</summary>
	public RunRecord Record(DateTimeOffset at, string folder, string command, double seconds, int exitCode, string? commit)
	{
		var measured = _samples.Skip(1).ToList();
		var cpu = RangeStats.Of([.. measured.Select(sample => sample.CpuPercent).Where(double.IsFinite)]);
		var memory = measured.Select(sample => sample.MemoryUsedBytes).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max();
		return new RunRecord(at, folder, command, seconds, exitCode, commit, cpu.IsEmpty ? double.NaN : cpu.Average, memory, _read, _written);
	}

	/// <summary>
	/// "  Left on   MSBuild ×3 410 MB, VBCSCompiler 180 MB · ids 4120 4188 4210 5532": the processes a command started that
	/// still run after it ended, by name, most memory first, with their ids to end them by; null when it left nothing.
	/// </summary>
	public static string? LeftRunning(IReadOnlyList<LeftProcess> processes)
	{
		const int MaximumNames = 4;
		const int MaximumIds = 8;
		if (processes.Count == 0)
		{
			return null;
		}

		var culture = CultureInfo.CurrentCulture;
		var names = processes.GroupBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
			.Select(group => (Name: group.Key, Count: group.Count(), Bytes: group.Sum(process => (double) process.Bytes)))
			.OrderByDescending(group => group.Bytes)
			.ToList();
		var listed = string.Join(", ", names.Take(MaximumNames).Select(group => $"{(group.Count > 1 ? $"{group.Name} ×{group.Count}" : group.Name)} {Units.Bytes(group.Bytes, culture)}"));
		if (names.Count > MaximumNames)
		{
			listed += $", {names.Count - MaximumNames} more";
		}

		var ids = string.Join(' ', processes.Take(MaximumIds).Select(process => process.Id.ToString(CultureInfo.InvariantCulture)));
		return $"  Left on   {listed} · ids {ids}{(processes.Count > MaximumIds ? " …" : string.Empty)}";
	}

	private static double Amount(double perSecond, double seconds) => double.IsFinite(perSecond) ? Math.Max(0, perSecond) * seconds : 0;

	private static string Name(ProcessLoad load) => load.Count > 1 ? $"{load.Name} ×{load.Count}" : load.Name;
}
