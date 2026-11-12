using Pulse.Core.Diagnostics;

namespace Pulse.Terminal;

/// <summary>
/// Logs what drawing costs: frames per second, time per frame and bytes sent to the terminal. Written ten
/// seconds after start, then every ten minutes, so the log shows the real cost without growing quickly.
/// </summary>
internal sealed class RenderStatistics
{
	private const double FirstReportSeconds = 10;
	private const double ReportIntervalSeconds = 600;

	private readonly double _started = MonotonicClock.Now;
	private double _windowStart = MonotonicClock.Now;
	private double _nextReport = MonotonicClock.Now + FirstReportSeconds;
	private int _frames;
	private double _renderSeconds;
	private long _bytes;

	public void Add(double renderSeconds, int bytes, bool sixel)
	{
		_frames++;
		_renderSeconds += renderSeconds;
		_bytes += bytes;

		var now = MonotonicClock.Now;
		if (now < _nextReport)
		{
			return;
		}

		var elapsed = now - _windowStart;
		Log.Info($"Rendering ({(sixel ? "sixel" : "braille")}) after {now - _started:0} s: {_frames / elapsed:0.0} frames/s, "
				 + $"{_renderSeconds / _frames * 1000:0.00} ms per frame, {_bytes / elapsed / 1024:0.0} KB/s to the terminal.");
		_windowStart = now;
		_nextReport = now + ReportIntervalSeconds;
		_frames = 0;
		_renderSeconds = 0;
		_bytes = 0;
	}
}
