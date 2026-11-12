using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>The marks file that <c>tpulse -mark</c> and <c>tpulse -- command</c> write and every running Pulse reads.</summary>
public sealed class MarkLogTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "pulse-marks-" + Guid.NewGuid().ToString("N"));

	private string FilePath => Path.Combine(_folder, "marks.log");

	[Fact]
	public void Drain_ReadsOnlyTheMarksWrittenAfterItStarted()
	{
		// Arrange
		var now = DateTimeOffset.Now;
		MarkLog.Append(FilePath, now.AddMinutes(-5), "mark", "App", "before Pulse started");
		var log = new MarkLog(FilePath);
		MarkLog.Append(FilePath, now.AddSeconds(-5), "run", "App", "dotnet build");
		var events = new List<PulseEvent>();

		// Act
		log.Drain(100, now, events);
		log.Drain(101, now, events);

		// Assert
		var mark = Assert.Single(events);
		Assert.Equal(EventKind.Mark, mark.Kind);
		Assert.Equal(("run", "App", "dotnet build"), (mark.Action, mark.Source, mark.Text));
		Assert.Equal(95, mark.Time, 3);
	}

	[Fact]
	public void Drain_LeavesAHalfWrittenLineForLater()
	{
		// Arrange
		Directory.CreateDirectory(_folder);
		var log = new MarkLog(FilePath);
		var now = DateTimeOffset.Now;
		File.WriteAllText(FilePath, $"{now:O}\tmark\tApp\thalf");
		var events = new List<PulseEvent>();

		// Act
		log.Drain(10, now, events);
		File.AppendAllText(FilePath, " done\n");
		log.Drain(11, now, events);

		// Assert
		Assert.Equal("half done", Assert.Single(events).Text);
	}

	[Fact]
	public void Append_PastTheLimit_StartsOver_AndReadersFollow()
	{
		// Arrange
		Directory.CreateDirectory(_folder);
		File.WriteAllText(FilePath, new string('x', (int) MarkLog.MaximumLength + 10) + "\n");
		var log = new MarkLog(FilePath);
		var events = new List<PulseEvent>();

		// Act
		MarkLog.Append(FilePath, DateTimeOffset.Now, "mark", "App", "after the limit");
		log.Drain(10, DateTimeOffset.Now, events);

		// Assert
		Assert.True(new FileInfo(FilePath).Length < 200);
		Assert.Equal("after the limit", Assert.Single(events).Text);
	}

	[Theory]
	[InlineData("error", EventKind.Error)]
	[InlineData("critical", EventKind.Error)]
	[InlineData("mark", EventKind.Mark)]
	[InlineData("done", EventKind.Mark)]
	[InlineData("failed", EventKind.Mark)]
	[InlineData("spike", EventKind.Spike)]
	[InlineData("leak", EventKind.Leak)]
	[InlineData("commit", EventKind.Git)]
	public void KindOf_FollowsTheAction(string action, EventKind expected)
	{
		// Act & Assert
		Assert.Equal(expected, PulseEvent.KindOf(action));
	}

	[Fact]
	public void BelongsTo_DrawsSpikesAndLeaksOnTheirOwnGraphOnly()
	{
		// Arrange
		var at = DateTimeOffset.Now;
		var spike = new PulseEvent(1, at, EventKind.Spike, "CPU", "spike", "92%");
		var commit = new PulseEvent(1, at, EventKind.Git, "App", "commit", "fix");

		// Act & Assert
		Assert.True(spike.BelongsTo("CPU"));
		Assert.False(spike.BelongsTo("Disk"));
		Assert.True(commit.BelongsTo("Disk"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}
}
