namespace Pulse.Core.Processing;

/// <summary>Rounds a scale up to the next 1-2-5 step (1, 2, 5, 10, 20, 50, ...), which reads well as a label.</summary>
public static class NiceScale
{
	private const double Tolerance = 1e-9;

	/// <summary>The smallest 1-2-5 step at or above <paramref name="value"/>; 1 for values that are not positive.</summary>
	public static double Ceiling(double value)
	{
		if (!double.IsFinite(value) || value <= 0)
		{
			return 1;
		}

		var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
		var fraction = value / magnitude;
		var step = fraction <= 1 + Tolerance ? 1
			: fraction <= 2 + Tolerance ? 2
			: fraction <= 5 + Tolerance ? 5
			: 10;
		return step * magnitude;
	}
}
