namespace Pulse.Core.Processing;

/// <summary>
/// The vertical scale of one graph. A fixed scale (percentages) never moves. An automatic scale (throughput)
/// follows the peak of the visible window: it grows at once to the next 1-2-5 step when data needs room,
/// shrinks only when the data would fit comfortably in a smaller step (so it does not flap at a boundary),
/// never drops below its floor (so idle noise is not blown up), and eases between steps in log space.
/// </summary>
public sealed class AutoScale
{
	/// <summary>Room kept above the peak.</summary>
	public const double Headroom = 1.15;

	/// <summary>A smaller step is taken only when the peak with headroom uses at most 1 / 1.6 of it.</summary>
	public const double ShrinkMargin = 1.6;

	private const double TimeConstantSeconds = 0.18;
	private const double SnapLogDistance = 0.003;

	private readonly double _floor;
	private readonly double _ceiling;

	private AutoScale(double initial, double floor, bool isFixed, double ceiling = double.PositiveInfinity)
	{
		Target = initial;
		Current = initial;
		_floor = floor;
		_ceiling = ceiling;
		IsFixed = isFixed;
	}

	public bool IsFixed { get; }

	/// <summary>The scale the graph is heading to; the value a scale label shows.</summary>
	public double Target { get; private set; }

	/// <summary>The scale to draw with right now (eases toward <see cref="Target"/>).</summary>
	public double Current { get; private set; }

	public static AutoScale Fixed(double maximum) => new(maximum, maximum, isFixed: true);

	public static AutoScale Automatic(double floor) => new(NiceScale.Ceiling(floor), floor, isFixed: false);

	/// <summary>
	/// An automatic scale that stops at <paramref name="ceiling"/> while the data stays within it: one process at 2 %
	/// gets a 5 % scale, but a peak of 95 % gets 100 %, not 200 %. Data above the ceiling still grows the scale.
	/// </summary>
	public static AutoScale Automatic(double floor, double ceiling) => new(NiceScale.Ceiling(floor), floor, isFixed: false, ceiling);

	/// <summary>Feeds the peak of the visible window (NaN when there is no data).</summary>
	public void Observe(double peak)
	{
		if (IsFixed)
		{
			return;
		}

		var value = double.IsFinite(peak) && peak > 0 ? peak : 0;
		var grown = Cap(NiceScale.Ceiling(Math.Max(value * Headroom, _floor)), value);
		if (grown > Target)
		{
			Target = grown;
			return;
		}

		var shrunk = Cap(NiceScale.Ceiling(Math.Max(value * Headroom * ShrinkMargin, _floor)), value);
		if (shrunk < Target)
		{
			Target = shrunk;
		}
	}

	/// <summary>Moves <see cref="Current"/> toward <see cref="Target"/>; true while the scale was changing.</summary>
	public bool Advance(double deltaSeconds)
	{
		if (Current == Target)
		{
			return false;
		}

		var factor = 1 - Math.Exp(-Math.Max(deltaSeconds, 0) / TimeConstantSeconds);
		var current = Math.Log(Current);
		var target = Math.Log(Target);
		current += (target - current) * factor;
		Current = Math.Abs(target - current) < SnapLogDistance ? Target : Math.Exp(current);
		return true;
	}

	/// <summary>Keeps a scale at the ceiling while the peak is within it.</summary>
	private double Cap(double scale, double peak) => peak <= _ceiling ? Math.Min(scale, _ceiling) : scale;
}
