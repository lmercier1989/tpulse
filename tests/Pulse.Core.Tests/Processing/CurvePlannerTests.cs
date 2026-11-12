using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class CurvePlannerTests
{
	private const double Tolerance = 1e-9;

	// One sample per second, 100 px per second, 0..100 mapped to y 100..0 (pixel y points down).
	private static readonly PlotMapping UnitMap = new(RightTime: 1, Right: 100, PixelsPerSecond: 100, Top: 0, Bottom: 100, PixelsPerUnit: 1);

	[Fact]
	public void Emit_TwoSamples_DrawsStraightCubic()
	{
		// Arrange
		var planner = new CurvePlanner();
		planner.Plan(Create(0, 100), UnitMap, maxGapSeconds: 5);
		var sink = new RecordingSink([]);

		// Act
		planner.Emit(ref sink, filled: false, baseline: 100);

		// Assert
		Assert.Equal(2, sink.Commands.Count);
		Assert.Equal(new Command("begin", 0, 100, false), sink.Commands[0]);
		var curve = sink.Commands[1];
		Assert.Equal("bezier", curve.Kind);
		Assert.Equal(100 / 3.0, curve.X1, Tolerance);
		Assert.Equal(100 - (100 / 3.0), curve.Y1, Tolerance);
		Assert.Equal(200 / 3.0, curve.X2, Tolerance);
		Assert.Equal(100 - (200 / 3.0), curve.Y2, Tolerance);
		Assert.Equal(100, curve.X, Tolerance);
		Assert.Equal(0, curve.Y, Tolerance);
	}

	[Fact]
	public void Emit_Filled_ClosesThroughBaselineWithUnstrokedEdges()
	{
		// Arrange
		var planner = new CurvePlanner();
		planner.Plan(Create(20, 60), UnitMap, maxGapSeconds: 5);
		var sink = new RecordingSink([]);

		// Act
		planner.Emit(ref sink, filled: true, baseline: 100);

		// Assert
		Assert.Equal(new Command("begin", 0, 100, true), sink.Commands[0]);
		Assert.Equal(new Command("line", 0, 80, false), sink.Commands[1]);
		Assert.Equal("bezier", sink.Commands[2].Kind);
		Assert.Equal(new Command("line", 100, 100, false), sink.Commands[3]);
	}

	[Fact]
	public void Plan_NoisySignal_CurveStaysBetweenNeighbouringSamples()
	{
		// Arrange
		double[] values = [10, 80, 20, 60, 60, 5, 90, 50, 51, 49, 100, 0];
		var series = Create(values);
		var map = UnitMap with { RightTime = values.Length - 1, Right = (values.Length - 1) * 100 };
		var planner = new CurvePlanner();

		// Act
		planner.Plan(series, map, maxGapSeconds: 5);

		// Assert: the interpolation never overshoots, so it never shows a value that was not measured.
		for (var k = 0; k < values.Length - 1; k++)
		{
			var low = map.Y(Math.Max(values[k], values[k + 1]));
			var high = map.Y(Math.Min(values[k], values[k + 1]));
			for (var x = k * 100.0; x <= (k + 1) * 100.0; x += 0.5)
			{
				Assert.True(planner.TryGetY(x, out var y));
				Assert.InRange(y, low - Tolerance, high + Tolerance);
			}
		}
	}

	[Fact]
	public void TryGetY_AtSampleTimes_ReturnsTheSamples()
	{
		// Arrange
		double[] values = [10, 80, 20, 60];
		var map = UnitMap with { RightTime = 3, Right = 300 };
		var planner = new CurvePlanner();
		planner.Plan(Create(values), map, maxGapSeconds: 5);

		// Act & Assert
		for (var k = 0; k < values.Length; k++)
		{
			Assert.True(planner.TryGetY(k * 100, out var y));
			Assert.Equal(map.Y(values[k]), y, Tolerance);
		}
	}

	[Fact]
	public void Plan_NaNSample_SplitsTheTrace()
	{
		// Arrange
		var planner = new CurvePlanner();
		var map = UnitMap with { RightTime = 4, Right = 400 };

		// Act
		planner.Plan(Create(1, 2, double.NaN, 3, 4), map, maxGapSeconds: 5);

		// Assert
		Assert.Equal(2, planner.RunCount);
		Assert.False(planner.TryGetY(200, out _));
	}

	[Fact]
	public void Plan_LoneSamplesBetweenGaps_AreNotDrawn()
	{
		// Arrange
		var planner = new CurvePlanner();
		var map = UnitMap with { RightTime = 5, Right = 500 };

		// Act
		planner.Plan(Create(1, double.NaN, 2, double.NaN, 3, 4), map, maxGapSeconds: 5);

		// Assert
		Assert.Equal(1, planner.RunCount);
		Assert.Equal(2, planner.PointCount);
	}

	[Fact]
	public void Plan_SilenceLongerThanGap_SplitsTheTrace()
	{
		// Arrange
		var series = new Series(16);
		foreach (var time in new[] { 0.0, 1, 2, 10, 11 })
		{
			series.Add(time, 50);
		}

		var map = UnitMap with { RightTime = 11, Right = 1100 };
		var planner = new CurvePlanner();

		// Act
		planner.Plan(series, map, maxGapSeconds: 3);

		// Assert
		Assert.Equal(2, planner.RunCount);
		Assert.False(planner.TryGetY(500, out _));
	}

	[Fact]
	public void Plan_StartsWithOneSampleLeftOfTheEdge()
	{
		// Arrange: the left edge is at t = -0.5.
		var series = new Series(16);
		for (var time = -4; time <= 2; time++)
		{
			series.Add(time, 50);
		}

		var map = new PlotMapping(RightTime: 2, Right: 100, PixelsPerSecond: 40, Top: 0, Bottom: 100, PixelsPerUnit: 1);
		var planner = new CurvePlanner();

		// Act
		planner.Plan(series, map, maxGapSeconds: 5);

		// Assert: t = -1, 0, 1 and 2; the trace reaches the edge but no further back.
		Assert.Equal(4, planner.PointCount);
		Assert.True(planner.TryGetY(0, out _));
	}

	[Fact]
	public void Plan_DenseSamples_KeepsEveryPeakWithBoundedPoints()
	{
		// Arrange: 2000 samples across 100 px, one spike to 100 and one drop to 0.
		var series = new Series(4096);
		for (var i = 0; i < 2000; i++)
		{
			var value = i switch
			{
				1234 => 100,
				777 => 0,
				_ => 40 + (10 * Math.Sin(i * 0.37)),
			};
			series.Add(i * 0.05, value);
		}

		var map = new PlotMapping(RightTime: 1999 * 0.05, Right: 100, PixelsPerSecond: 1, Top: 0, Bottom: 100, PixelsPerUnit: 1);
		var planner = new CurvePlanner();
		var sink = new RecordingSink([]);

		// Act
		planner.Plan(series, map, maxGapSeconds: 5);
		planner.Emit(ref sink, filled: false, baseline: 100);

		// Assert
		Assert.True(planner.IsDense);
		Assert.InRange(planner.PointCount, 2, 4 * 102);
		Assert.Contains(sink.Commands, command => Math.Abs(command.Y - map.Y(100)) < Tolerance);
		Assert.Contains(sink.Commands, command => Math.Abs(command.Y - map.Y(0)) < Tolerance);
	}

	[Fact]
	public void TryGetEnd_ReturnsNewestPlannedPoint()
	{
		// Arrange
		var planner = new CurvePlanner();
		var map = UnitMap with { RightTime = 1, Right = 100 };
		planner.Plan(Create(10, 30, 70), map, maxGapSeconds: 5);

		// Act
		var found = planner.TryGetEnd(out var x, out var y);

		// Assert
		Assert.True(found);
		Assert.Equal(200, x, Tolerance);
		Assert.Equal(map.Y(70), y, Tolerance);
	}

	[Fact]
	public void Plan_EmptySeries_PlansNothing()
	{
		// Arrange
		var planner = new CurvePlanner();

		// Act
		planner.Plan(new Series(4), UnitMap, maxGapSeconds: 5);

		// Assert
		Assert.Equal(0, planner.RunCount);
		Assert.False(planner.TryGetEnd(out _, out _));
	}

	private static Series Create(params double[] values)
	{
		var series = new Series(Math.Max(4, values.Length));
		for (var i = 0; i < values.Length; i++)
		{
			series.Add(i, values[i]);
		}

		return series;
	}

	private readonly record struct Command(string Kind, double X, double Y, bool Flag, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0);

	private readonly struct RecordingSink(List<Command> commands) : IPathSink
	{
		public List<Command> Commands { get; } = commands;

		public void BeginFigure(double x, double y, bool filled) => Commands.Add(new Command("begin", x, y, filled));

		public void LineTo(double x, double y, bool stroked) => Commands.Add(new Command("line", x, y, stroked));

		public void BezierTo(double x1, double y1, double x2, double y2, double x3, double y3)
			=> Commands.Add(new Command("bezier", x3, y3, true, x1, y1, x2, y2));
	}
}
