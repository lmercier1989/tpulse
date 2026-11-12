using System.Diagnostics;

namespace Pulse.Core.Diagnostics;

/// <summary>
/// The one time base shared by sampling and rendering: seconds from the high-resolution performance
/// counter. It never jumps with wall-clock changes, so scrolling stays smooth across DST or NTP corrections.
/// </summary>
public static class MonotonicClock
{
	private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

	public static double Now => Stopwatch.GetTimestamp() * TicksToSeconds;
}
