namespace Pulse.Core.Processing;

/// <summary>One measurement: monotonic time in seconds and its value. NaN marks a gap in the trace.</summary>
public readonly record struct Sample(double Time, double Value);

/// <summary>Minimum and maximum of the finite values in a time window; both NaN when there are none.</summary>
public readonly record struct ValueRange(double Min, double Max)
{
	public static ValueRange Empty { get; } = new(double.NaN, double.NaN);

	public bool IsEmpty => double.IsNaN(Min);
}
