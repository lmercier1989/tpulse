using System.Diagnostics;

using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>
/// Turns the CPU time of processes in successive snapshots into a load in % of the whole machine, like Task Manager.
/// A process is known by its id and creation time, so a reused id never inherits the time of the process before it;
/// a process started since the previous snapshot counts with all of its time. Call <see cref="Begin"/>, then
/// <see cref="Percent"/> for every process of interest, then <see cref="End"/>.
/// </summary>
internal sealed class ProcessCpuMeter
{
	private readonly int _processors = Environment.ProcessorCount;
	private Dictionary<(int Id, long Created), long> _previous = [];
	private Dictionary<(int Id, long Created), long> _current = [];
	private long _lastSample;
	private long _lastSampleTime;
	private long _sample;
	private long _sampleTime;
	private long _elapsed;

	/// <summary>Starts a snapshot; false for the first one, which has no interval to measure over yet.</summary>
	public bool Begin()
	{
		_sample = Stopwatch.GetTimestamp();
		_sampleTime = TimeProvider.System.GetUtcNow().UtcDateTime.ToFileTimeUtc();
		_elapsed = _lastSample == 0 ? 0 : Stopwatch.GetElapsedTime(_lastSample, _sample).Ticks;
		_current.Clear();
		return _elapsed > 0;
	}

	/// <summary>The CPU of <paramref name="record"/> since the previous snapshot, in % of the machine; 0 on the first.</summary>
	public double Percent(in ProcessRecord record)
	{
		var key = (record.Id, record.CreateTime);
		_current[key] = record.CpuTime;
		long delta = 0;
		if (_previous.TryGetValue(key, out var previous))
		{
			delta = Math.Max(0, record.CpuTime - previous);
		}
		else if (_lastSampleTime != 0 && record.CreateTime >= _lastSampleTime)
		{
			delta = record.CpuTime; // Started since the last snapshot: all of its time falls in this interval.
		}

		return _elapsed > 0 ? delta * 100.0 / (_elapsed * (double) _processors) : 0;
	}

	/// <summary>Ends the snapshot; processes not seen in it are forgotten.</summary>
	public void End()
	{
		(_previous, _current) = (_current, _previous);
		_lastSample = _sample;
		_lastSampleTime = _sampleTime;
	}
}
