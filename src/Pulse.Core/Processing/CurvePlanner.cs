namespace Pulse.Core.Processing;

/// <summary>Receives the figures of a planned curve; implemented over the renderer's geometry builder.</summary>
public interface IPathSink
{
	/// <summary>Starts a figure. A filled figure is closed by its fill, never by a stroke.</summary>
	void BeginFigure(double x, double y, bool filled);

	void LineTo(double x, double y, bool stroked);

	void BezierTo(double x1, double y1, double x2, double y2, double x3, double y3);
}

/// <summary>
/// Plans the waveform of one series in pixel space and emits it as path figures.
/// <list type="bullet">
/// <item>Between samples it draws a monotone cubic (Steffen 1990, the same curve as d3's curveMonotoneX):
/// smooth, but it never rises above or dips below the two samples it connects, so it invents no peaks.</item>
/// <item>The trace breaks at a NaN sample or a time gap, instead of drawing a line across missing data.</item>
/// <item>When samples are denser than two pixels it switches to straight lines through the first, lowest,
/// highest and last sample of every pixel column, which keeps every peak while bounding the work.</item>
/// </list>
/// Buffers are reused between plans, so planning does not allocate once warmed up.
/// </summary>
public sealed class CurvePlanner
{
	private const double DenseSpacingPixels = 2.0;
	private const double MinimumStep = 1e-6;

	private readonly List<Run> _runs = [];
	private double[] _x = new double[256];
	private double[] _y = new double[256];
	private double[] _m = new double[256];
	private int _count;
	private int _runStart;
	private bool _dense;

	private bool _bucketOpen;
	private long _bucket;
	private double _firstX, _firstY, _lastX, _lastY, _minX, _minY, _maxX, _maxY;

	/// <summary>Points planned (after decimation).</summary>
	public int PointCount => _count;

	/// <summary>Connected stretches of trace.</summary>
	public int RunCount => _runs.Count;

	/// <summary>Whether the last plan used the dense (per-pixel min/max, straight line) mode.</summary>
	public bool IsDense => _dense;

	/// <summary>
	/// Plans every sample from just left of the plot to the newest one. Samples beyond the right edge are
	/// kept: they scroll into view before the next plan.
	/// </summary>
	public void Plan(Series series, in PlotMapping map, double maxGapSeconds)
	{
		_count = 0;
		_runStart = 0;
		_runs.Clear();
		_bucketOpen = false;

		var samples = series.Samples;
		if (samples.Count == 0 || !(map.PixelsPerSecond > 0))
		{
			return;
		}

		// One sample left of the edge, so the trace reaches the edge instead of starting inside the plot.
		var start = Math.Max(0, series.LowerBound(map.TimeAt(0)) - 1);
		var span = samples.Newest.Time - samples[start].Time;
		var points = samples.Count - start;
		_dense = points > 2 && span * map.PixelsPerSecond / (points - 1) < DenseSpacingPixels;

		var previousTime = double.NaN;
		for (var i = start; i < samples.Count; i++)
		{
			ref readonly var sample = ref samples[i];
			if (!double.IsFinite(sample.Value))
			{
				CloseRun();
				previousTime = double.NaN;
				continue;
			}

			if (!double.IsNaN(previousTime) && sample.Time - previousTime > maxGapSeconds)
			{
				CloseRun();
			}

			previousTime = sample.Time;
			var x = map.X(sample.Time);
			var y = map.Y(sample.Value);
			if (_dense)
			{
				AddToBucket((long) Math.Floor(sample.Time * map.PixelsPerSecond), x, y);
			}
			else
			{
				Append(x, y);
			}
		}

		CloseRun();
	}

	/// <summary>
	/// Emits the planned runs. A filled figure starts and ends on <paramref name="baseline"/> with unstroked
	/// edges, so one geometry serves as both the gradient fill and the stroked trace.
	/// </summary>
	public void Emit<TSink>(ref TSink sink, bool filled, double baseline)
		where TSink : struct, IPathSink
	{
		foreach (var run in _runs)
		{
			var first = run.Start;
			var last = run.Start + run.Length - 1;
			if (filled)
			{
				sink.BeginFigure(_x[first], baseline, filled: true);
				sink.LineTo(_x[first], _y[first], stroked: false);
			}
			else
			{
				sink.BeginFigure(_x[first], _y[first], filled: false);
			}

			for (var k = first; k < last; k++)
			{
				if (run.Linear)
				{
					sink.LineTo(_x[k + 1], _y[k + 1], stroked: true);
					continue;
				}

				var third = (_x[k + 1] - _x[k]) / 3;
				sink.BezierTo(
					_x[k] + third,
					_y[k] + (_m[k] * third),
					_x[k + 1] - third,
					_y[k + 1] - (_m[k + 1] * third),
					_x[k + 1],
					_y[k + 1]);
			}

			if (filled)
			{
				sink.LineTo(_x[last], baseline, stroked: false);
			}
		}
	}

	/// <summary>
	/// Samples the curve at x = 0, 1, 2, ... into <paramref name="destination"/>; NaN where there is no trace.
	/// Used by the pixel and character renderers, which draw the curve column by column.
	/// </summary>
	public void SampleColumns(Span<double> destination)
	{
		for (var x = 0; x < destination.Length; x++)
		{
			destination[x] = TryGetY(x, out var y) ? y : double.NaN;
		}
	}

	/// <summary>The last planned point, where the trace currently ends.</summary>
	public bool TryGetEnd(out double x, out double y)
	{
		if (_runs.Count == 0)
		{
			x = y = 0;
			return false;
		}

		var run = _runs[^1];
		var last = run.Start + run.Length - 1;
		x = _x[last];
		y = _y[last];
		return true;
	}

	/// <summary>The y of the emitted curve at <paramref name="x"/>; false in a gap or outside the trace.</summary>
	public bool TryGetY(double x, out double y)
	{
		for (var r = _runs.Count - 1; r >= 0; r--)
		{
			var run = _runs[r];
			var first = run.Start;
			var last = run.Start + run.Length - 1;
			if (x > _x[last])
			{
				break; // Runs are ordered by x, so x lies in the gap after this run.
			}

			if (x < _x[first])
			{
				continue;
			}

			var k = SegmentAt(first, last, x);
			var h = _x[k + 1] - _x[k];
			var t = h > 0 ? (x - _x[k]) / h : 0;
			y = run.Linear ? _y[k] + ((_y[k + 1] - _y[k]) * t) : Hermite(k, h, t);
			return true;
		}

		y = 0;
		return false;
	}

	private double Hermite(int k, double h, double t)
	{
		var t2 = t * t;
		var t3 = t2 * t;
		return (((2 * t3) - (3 * t2) + 1) * _y[k])
			   + ((t3 - (2 * t2) + t) * h * _m[k])
			   + (((-2 * t3) + (3 * t2)) * _y[k + 1])
			   + ((t3 - t2) * h * _m[k + 1]);
	}

	/// <summary>The largest k in [first, last - 1] with x[k] at or before <paramref name="x"/>.</summary>
	private int SegmentAt(int first, int last, double x)
	{
		int low = first, high = last - 1;
		while (low < high)
		{
			var middle = (low + high + 1) >>> 1;
			if (_x[middle] <= x)
			{
				low = middle;
			}
			else
			{
				high = middle - 1;
			}
		}

		return low;
	}

	private void Append(double x, double y)
	{
		if (_count > _runStart && x <= _x[_count - 1] + MinimumStep)
		{
			return;
		}

		if (_count == _x.Length)
		{
			var size = _x.Length * 2;
			Array.Resize(ref _x, size);
			Array.Resize(ref _y, size);
			Array.Resize(ref _m, size);
		}

		_x[_count] = x;
		_y[_count] = y;
		_count++;
	}

	private void AddToBucket(long bucket, double x, double y)
	{
		if (_bucketOpen && bucket != _bucket)
		{
			FlushBucket();
		}

		if (!_bucketOpen)
		{
			_bucketOpen = true;
			_bucket = bucket;
			_firstX = _lastX = _minX = _maxX = x;
			_firstY = _lastY = _minY = _maxY = y;
			return;
		}

		_lastX = x;
		_lastY = y;
		if (y < _minY)
		{
			_minX = x;
			_minY = y;
		}

		if (y > _maxY)
		{
			_maxX = x;
			_maxY = y;
		}
	}

	private void FlushBucket()
	{
		if (!_bucketOpen)
		{
			return;
		}

		_bucketOpen = false;
		Append(_firstX, _firstY);
		if (_minX <= _maxX)
		{
			Append(_minX, _minY);
			Append(_maxX, _maxY);
		}
		else
		{
			Append(_maxX, _maxY);
			Append(_minX, _minY);
		}

		Append(_lastX, _lastY);
	}

	private void CloseRun()
	{
		FlushBucket();
		var length = _count - _runStart;
		if (length >= 2)
		{
			_runs.Add(new Run(_runStart, length, _dense));
			if (!_dense)
			{
				ComputeTangents(_runStart, length);
			}
		}
		else
		{
			_count = _runStart; // A lone sample has no segment to draw.
		}

		_runStart = _count;
	}

	/// <summary>
	/// Steffen's tangents: zero at a local extremum, otherwise bounded so the cubic stays monotone between
	/// neighbouring samples. End points use the slope of their only segment, which keeps that segment monotone too.
	/// </summary>
	private void ComputeTangents(int start, int length)
	{
		var end = start + length - 1;
		for (var i = start + 1; i < end; i++)
		{
			var h0 = _x[i] - _x[i - 1];
			var h1 = _x[i + 1] - _x[i];
			var s0 = (_y[i] - _y[i - 1]) / h0;
			var s1 = (_y[i + 1] - _y[i]) / h1;
			var p = ((s0 * h1) + (s1 * h0)) / (h0 + h1);
			_m[i] = (Math.Sign(s0) + Math.Sign(s1)) * Math.Min(Math.Min(Math.Abs(s0), Math.Abs(s1)), 0.5 * Math.Abs(p));
		}

		_m[start] = Slope(start);
		_m[end] = Slope(end - 1);
	}

	private double Slope(int i) => (_y[i + 1] - _y[i]) / (_x[i + 1] - _x[i]);

	private readonly record struct Run(int Start, int Length, bool Linear);
}
