namespace Pulse.Core.Processing;

/// <summary>
/// Turns cumulative per-interface counters (bytes in and out since boot) into rates. Only interfaces present
/// in both samples count, and a counter that went backwards (reset, driver reload) is skipped instead of
/// producing a huge bogus spike. Reuses its dictionaries, so steady-state sampling does not allocate.
/// </summary>
public sealed class CounterRate
{
	private Dictionary<ulong, (ulong In, ulong Out)> _previous = [];
	private Dictionary<ulong, (ulong In, ulong Out)> _current = [];
	private bool _hasPrevious;

	/// <summary>Starts a new sample.</summary>
	public void Begin() => _current.Clear();

	/// <summary>Adds the cumulative totals of one interface to the current sample.</summary>
	public void Add(ulong key, ulong inTotal, ulong outTotal) => _current[key] = (inTotal, outTotal);

	/// <summary>Completes the sample; the rates per second, or NaN for the first sample.</summary>
	public (double InPerSecond, double OutPerSecond) Complete(double elapsedSeconds)
	{
		var result = (double.NaN, double.NaN);
		if (_hasPrevious && elapsedSeconds > 0)
		{
			ulong inDelta = 0, outDelta = 0;
			foreach (var (key, now) in _current)
			{
				if (!_previous.TryGetValue(key, out var before))
				{
					continue;
				}

				if (now.In >= before.In)
				{
					inDelta += now.In - before.In;
				}

				if (now.Out >= before.Out)
				{
					outDelta += now.Out - before.Out;
				}
			}

			result = (inDelta / elapsedSeconds, outDelta / elapsedSeconds);
		}

		(_previous, _current) = (_current, _previous);
		_hasPrevious = true;
		return result;
	}
}
