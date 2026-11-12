using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Presentation;

/// <summary>The git, top-process and event rows of the info panel, the git graph header and the alerts.</summary>
public sealed class ActivityPanelTests
{
	private static readonly DateTimeOffset Now = TimeProvider.System.GetUtcNow();

	private static readonly GitRepositoryStatus Busy = new(
		"Webshop", @"D:\Repos\Webshop", "feature/x", "rebasing", 7, 120, 30, 2, 0, TimeSpan.FromMinutes(3), Now.AddMinutes(-5), StatusKnown: true);

	private static readonly GitRepositoryStatus Clean = new("Pulse", @"D:\Repos\Pulse", "main", null, 0, 0, 0, null, null, null, null, StatusKnown: true);

	[Fact]
	public void Build_Git_ShowsBranchStateChangesAndAStaleLock()
	{
		// Arrange
		var sample = new MetricSnapshot { Time = 1, Git = new GitActivity([Busy, Clean]) };

		// Act
		var lines = InfoPanel.Build(null, sample, null);

		// Assert
		Assert.Contains(lines, line => line is { Label: "GIT", Kind: PanelLineKind.Section });
		Assert.Contains(lines, line => line is { Label: "Webshop", Text: "feature/x · rebasing", Right: "+120 −30", Warning: true });
		Assert.Contains(lines, line => line is { Kind: PanelLineKind.Note, Text: "7 files changed · ↑2 to push", Right: "last commit 5 min ago" });
		Assert.Contains(lines, line => line is { Text: "index.lock blocks git", Right: "3 min", Warning: true });
		Assert.Contains(lines, line => line is { Label: "Pulse", Text: "main", Right: "clean" });
	}

	[Fact]
	public void Build_TopProcessesAndEvents_AreListed()
	{
		// Arrange
		var sample = new MetricSnapshot
		{
			Time = 1,
			TopCpu = [new ProcessLoad("msedge", 24, 4.2), new ProcessLoad("devenv", 1, 1)],
			TopIo = [new ProcessLoad("sqlservr", 1, 12_000_000)],
		};
		PulseEvent[] events =
		[
			new(1, Now.AddSeconds(-30), EventKind.Error, "Application Error", "error", "Faulting application devenv.exe"),
			new(0, Now.AddMinutes(-2), EventKind.Git, "Pulse", "commit", "Fix the login"),
		];

		// Act
		var lines = InfoPanel.Build(null, sample, null, events);

		// Assert
		Assert.Contains(lines, line => line is { Label: "TOP CPU", Kind: PanelLineKind.Section });
		Assert.Contains(lines, line => line is { Label: "msedge", Text: "×24" } && line.Right.EndsWith('%'));
		Assert.Contains(lines, line => line is { Label: "devenv", Text: "" });
		Assert.Contains(lines, line => line.Label == "sqlservr" && line.Right.Contains("MB/s", StringComparison.Ordinal));
		Assert.Contains(lines, line => line is { Label: "error", Right: "30 s", Warning: true } && line.Text.StartsWith("Application Error · Faulting", StringComparison.Ordinal));
		Assert.Contains(lines, line => line is { Label: "commit", Text: "Pulse · Fix the login", Right: "2 min", Warning: false });
	}

	[Fact]
	public void BuildReplay_NamesTheFileAndTheMomentShown()
	{
		// Act
		var lines = InfoPanel.BuildReplay("run.csv", Now.AddHours(-1), Now, Now.AddMinutes(-10));

		// Assert
		Assert.Equal("REPLAY", lines[0].Label);
		Assert.Contains(lines, line => line is { Label: "file", Text: "run.csv" });
		Assert.Contains(lines, line => line is { Label: "recorded", Right: "1 h 0 min" });
		Assert.Contains(lines, line => line.Label == "shown");
	}

	[Theory]
	[InlineData(3 * 3600, "3 h")]
	[InlineData(3 * 86400, "3 d")]
	public void FormatAgo_HoursAndDays(int seconds, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, InfoPanel.FormatAgo(TimeSpan.FromSeconds(seconds)));
	}

	[Fact]
	public void Describe_Git_ShowsLinesAndTurnsRedOnAStaleLock()
	{
		// Arrange
		var info = new SystemInfo { CpuName = "CPU", LogicalProcessors = 8, FollowsGit = true, GitRepositories = ["Webshop", "Pulse"] };
		var sample = new MetricSnapshot { Time = 1, Git = new GitActivity([Busy, Clean]), GitChangedLines = 150 };

		// Act
		var readout = ChannelText.Describe(Channel.ForGit(), info, sample, showStats: false);

		// Assert
		Assert.Equal("Git", readout.Title);
		Assert.Equal("150 lines", readout.PrimaryValue);
		Assert.Equal("Webshop feature/x rebasing · Pulse main", readout.Detail);
		Assert.True(readout.Alert);
	}

	[Fact]
	public void Describe_GitWithoutRepositories_SaysHowToStartIt()
	{
		// Act
		var readout = ChannelText.Describe(Channel.ForGit(), new SystemInfo { CpuName = "CPU", LogicalProcessors = 8 }, null, showStats: false);

		// Assert
		Assert.Contains("-git", readout.Unavailable, StringComparison.Ordinal);
	}

	[Fact]
	public void AlertMonitor_RaisesOnceWhenAnAlertStarts_AndQuietsItsRepeats()
	{
		// Arrange
		var monitor = new AlertMonitor();
		var full = new SystemDetails("pc", "Windows", [new DiskSpace("C:", string.Empty, 100, 5)], null, null);
		var empty = full with { Disks = [new DiskSpace("C:", string.Empty, 100, 50)] };

		// Act
		var first = monitor.Check([], new MetricSnapshot { Time = 1, Details = full }, Now);
		var lasting = monitor.Check([], new MetricSnapshot { Time = 2, Details = full }, Now.AddSeconds(1));
		monitor.Check([], new MetricSnapshot { Time = 3, Details = empty }, Now.AddSeconds(2));
		var soonAgain = monitor.Check([], new MetricSnapshot { Time = 4, Details = full }, Now.AddMinutes(1));
		monitor.Check([], new MetricSnapshot { Time = 5, Details = empty }, Now.AddMinutes(2));
		var later = monitor.Check([], new MetricSnapshot { Time = 6, Details = full }, Now.AddMinutes(10));

		// Assert
		Assert.Equal("Disk C: 95% full", Assert.Single(first).Title);
		Assert.Empty(lasting);
		Assert.Empty(soonAgain);
		Assert.Single(later);
	}

	[Fact]
	public void AlertMonitor_LastingCpuLoad_AndAStaleLock_AreRaised()
	{
		// Arrange
		var monitor = new AlertMonitor();
		var cpu = Channel.Create(MetricKind.Cpu);
		for (var t = 0; t <= 12; t++)
		{
			cpu.Append(t, 97);
		}

		// Act
		var alerts = monitor.Check([cpu], new MetricSnapshot { Time = 12, Git = new GitActivity([Busy]) }, Now);

		// Assert
		Assert.Equal(["CPU at 97%", "git is blocked in Webshop"], alerts.Select(alert => alert.Title));
	}
}
