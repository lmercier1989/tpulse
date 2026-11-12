using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>Spikes: a graph jumping well above its own level, and who caused it. Leaks: memory that keeps climbing.</summary>
public sealed class SpikeAndLeakTests
{
	private static readonly DateTimeOffset Now = DateTimeOffset.Now;

	[Fact]
	public void Check_CpuJump_ReportsOnceAndNamesTheCause()
	{
		// Arrange: 15 quiet samples, then dotnet takes the CPU for three samples.
		var detector = new SpikeDetector();
		var events = new List<PulseEvent>();
		for (var i = 0; i < 15; i++)
		{
			detector.Check(Cpu(i, 10, new ProcessLoad("dotnet", 3, 2), new ProcessLoad("msedge", 20, 5)), Now, events);
		}

		// Act
		for (var i = 15; i < 18; i++)
		{
			detector.Check(Cpu(i, 90, new ProcessLoad("dotnet", 3, 75), new ProcessLoad("msedge", 20, 6)), Now, events);
		}

		// Assert
		var spike = Assert.Single(events);
		Assert.Equal((EventKind.Spike, "CPU", "spike"), (spike.Kind, spike.Source, spike.Action));
		Assert.Equal(15, spike.Time);
		Assert.Contains("dotnet ×3 +", spike.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void Check_SmallRiseOrTooFewSamples_ReportsNothing()
	{
		// Arrange
		var detector = new SpikeDetector();
		var events = new List<PulseEvent>();

		// Act: a jump before the level is known, then a rise of 20 points.
		detector.Check(Cpu(0, 5), Now, events);
		detector.Check(Cpu(1, 95), Now, events);
		for (var i = 2; i < 20; i++)
		{
			detector.Check(Cpu(i, 30), Now, events);
		}

		detector.Check(Cpu(20, 50), Now, events);

		// Assert
		Assert.Empty(events);
	}

	[Fact]
	public void Cause_IsTheLargestRiseOverTheAverageBefore()
	{
		// Arrange
		IReadOnlyList<ProcessLoad>[] before = [[new ProcessLoad("sql", 1, 50)], [new ProcessLoad("sql", 1, 50)]];
		ProcessLoad[] now = [new("sql", 1, 60), new("backup", 1, 40)];

		// Act
		var cause = SpikeDetector.Cause(now, before);

		// Assert
		Assert.Equal("backup", cause!.Value.Load.Name);
		Assert.Equal(40, cause.Value.Rise);
		Assert.Equal(2.5, SpikeDetector.Median([4, 1, 2, 3]));
	}

	[Fact]
	public void Evaluate_SteadyClimbOverTenMinutes_IsALeak()
	{
		// Arrange: 500 MB growing 1 MB per second, with a little noise.
		var points = Enumerable.Range(0, 61).Select(i => (Time: i * 10.0, Bytes: Mb(500 + (i * 10) + (i % 3 == 0 ? 4 : -4)))).ToList();

		// Act
		var leak = LeakDetector.Evaluate(points, out var growth, out var minutes);

		// Assert
		Assert.True(leak);
		Assert.InRange(growth, Mb(580), Mb(620));
		Assert.Equal(10, minutes, 3);
	}

	[Fact]
	public void Evaluate_FlatShortOrJumpy_IsNoLeak()
	{
		// Arrange
		var flat = Enumerable.Range(0, 61).Select(i => (i * 10.0, Mb(800 + (i % 2 == 0 ? 20 : -20)))).ToList();
		var short_ = Enumerable.Range(0, 30).Select(i => (i * 10.0, Mb(500 + (i * 20)))).ToList();
		var small = Enumerable.Range(0, 61).Select(i => (i * 10.0, Mb(1000 + i))).ToList();

		// Act & Assert
		Assert.False(LeakDetector.Evaluate(flat, out _, out _));
		Assert.False(LeakDetector.Evaluate(short_, out _, out _));
		Assert.False(LeakDetector.Evaluate(small, out _, out _)); // 60 MB in ten minutes is no leak.
	}

	private static double Mb(double megabytes) => megabytes * 1024 * 1024;

	private static MetricSnapshot Cpu(double time, double percent, params ProcessLoad[] top) => new() { Time = time, CpuPercent = percent, TopCpu = top };
}
