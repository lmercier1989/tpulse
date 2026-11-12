using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Processing;

public sealed class ChannelTests
{
	[Fact]
	public void Append_StoresBothTracesAndChangesVersion()
	{
		// Arrange
		var channel = new Channel(MetricKind.Disk, AutoScale.Automatic(1_000_000), hasSecondary: true, capacity: 8);
		var version = channel.Version;

		// Act
		channel.Append(1, 10, 20);

		// Assert
		Assert.NotEqual(version, channel.Version);
		Assert.Equal(10, channel.Primary.Latest);
		Assert.Equal(20, channel.Secondary!.Latest);
	}

	[Fact]
	public void AppendCores_CreatesOneTracePerCore()
	{
		// Arrange
		var channel = new Channel(MetricKind.Cpu, AutoScale.Fixed(100), hasSecondary: false, capacity: 8);

		// Act
		channel.AppendCores(1, [10, 20, 30]);
		channel.AppendCores(2, [11, 21, 31]);

		// Assert
		Assert.Equal(3, channel.Cores.Length);
		Assert.Equal(31, channel.Cores[2].Latest);
		Assert.Equal(2, channel.Cores[0].Samples.Count);
	}

	[Fact]
	public void AppendBreak_BreaksEveryTrace()
	{
		// Arrange
		var channel = new Channel(MetricKind.Cpu, AutoScale.Fixed(100), hasSecondary: false, capacity: 8);
		channel.Append(1, 50);
		channel.AppendCores(1, [10, 20]);

		// Act
		channel.AppendBreak(2);

		// Assert
		Assert.True(double.IsNaN(channel.Primary.Latest));
		Assert.All(channel.Cores, core => Assert.True(double.IsNaN(core.Latest)));
	}

	[Theory]
	[InlineData(new[] { 95.0, 96, 97, 98, 99, 95, 94, 93, 92, 91, 90, 99 }, true)]
	[InlineData(new[] { 95.0, 96, 97, 98, 99, 95, 50, 93, 92, 91, 90, 99 }, false)]
	[InlineData(new[] { 99.0, 99, 99 }, false)]
	public void IsSustainedAbove_NeedsTheWholeWindowAtOrAboveTheLimit(double[] values, bool expected)
	{
		// Arrange: one sample per second.
		var channel = new Channel(MetricKind.Cpu, AutoScale.Fixed(100), hasSecondary: false, capacity: 32);
		for (var second = 0; second < values.Length; second++)
		{
			channel.Append(second, values[second]);
		}

		// Act
		var sustained = channel.IsSustainedAbove(90, seconds: 10, newestTime: values.Length - 1);

		// Assert
		Assert.Equal(expected, sustained);
	}

	[Fact]
	public void Append_Snapshot_PutsClaudesShareBesideTheTotal()
	{
		// Arrange
		var cpu = Channel.Create(MetricKind.Cpu);
		var claude = Channel.Create(MetricKind.Claude);
		var sample = new Core.Metrics.MetricSnapshot { Time = 1, CpuPercent = 40, ClaudeCpuPercent = 12, ClaudeOutputPerMinute = 900, ClaudeInputPerMinute = 3000 };

		// Act
		cpu.Append(sample);
		claude.Append(sample);

		// Assert
		Assert.Equal(12, cpu.Secondary!.Latest);
		Assert.Equal(900, claude.Primary.Latest);
		Assert.Equal(3000, claude.Secondary!.Latest);
	}

	[Fact]
	public void Refresh_UsesVisibleWindowForRangeAndScale()
	{
		// Arrange: a 90 MB/s burst long ago, then reads of up to 9 MB/s.
		var channel = new Channel(MetricKind.Disk, AutoScale.Automatic(1_000_000), hasSecondary: true, capacity: 32);
		channel.Append(0, 90_000_000, 0);
		for (var time = 1; time <= 10; time++)
		{
			channel.Append(time, time * 900_000, 100);
		}

		// Act: 5 s of history behind a 2 s delay covers t = 3 to t = 10.
		channel.Refresh(newestTime: 10, historySeconds: 5, delaySeconds: 2);

		// Assert
		Assert.Equal(new ValueRange(2_700_000, 9_000_000), channel.PrimaryRange);
		Assert.Equal(new ValueRange(100, 100), channel.SecondaryRange);
		Assert.Equal(20_000_000, channel.Scale.Target);
	}
}
