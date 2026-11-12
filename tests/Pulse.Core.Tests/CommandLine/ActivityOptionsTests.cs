using Pulse.Core.CommandLine;
using Pulse.Core.Metrics;
using Pulse.Core.Processing;
using Pulse.Core.Settings;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.CommandLine;

/// <summary>-git, -notify and -replay on the command line, and the event markers in the raster.</summary>
public sealed class ActivityOptionsTests
{
	[Fact]
	public void TryParse_GitWithoutFolder_PicksTheGitGraph_AndLeavesTheSearchToPulse()
	{
		// Act
		LaunchOptions.TryParse(["-git", "-cpu", "-git", "cpu"], out var options, out _);

		// Assert
		Assert.Empty(options.GitPaths);
		Assert.Equal([MetricKind.Cpu, MetricKind.Git], options.Metrics!.Order());
	}

	[Fact]
	public void TryParse_GitWithAFolder_FollowsOnlyThat()
	{
		// Act
		LaunchOptions.TryParse(["-git", Path.GetTempPath(), "-repo=D:\\Repos"], out var options, out _);

		// Assert
		Assert.Equal([Path.GetTempPath(), "D:\\Repos"], options.GitPaths);
		Assert.Equal([MetricKind.Git], options.Metrics!);
	}

	[Fact]
	public void TryParse_NotifyAndReplay()
	{
		// Act
		LaunchOptions.TryParse(["-notify", "-replay", "run.csv"], out var options, out _);
		var settings = new PulseSettings();
		options.ApplyTo(settings);

		// Assert
		Assert.True(options.Notify);
		Assert.True(settings.Notify);
		Assert.Equal("run.csv", options.ReplayPath);
	}

	[Fact]
	public void TryParse_ReplayWithoutFile_IsRefused()
	{
		// Act
		var parsed = LaunchOptions.TryParse(["-replay"], out _, out var error);

		// Assert
		Assert.False(parsed);
		Assert.Contains("-replay", error);
	}

	[Fact]
	public void DrawMarker_DrawsADashedLine_ThatATraceCovers()
	{
		// Arrange
		var raster = new ScopeRaster(ScopePalette.Instance);
		raster.Reset(20, 24);
		var marker = ScopePalette.Marker(EventKind.Git);

		// Act
		raster.DrawMarker(10, 0, 23, marker);
		raster.DrawDot(10, 20, 1.5, ScopePalette.Stroke(MetricKind.Cpu, 1), ScopePalette.StrokeLevels);

		// Assert
		Assert.Equal(marker, raster.Pixels[(0 * 20) + 10]);
		Assert.Equal(marker, raster.Pixels[(3 * 20) + 10]);
		Assert.Equal(ScopePalette.Background, raster.Pixels[(4 * 20) + 10]);
		Assert.Equal(ScopePalette.Stroke(MetricKind.Cpu, ScopePalette.StrokeLevels), raster.Pixels[(20 * 20) + 10]);
		Assert.NotEqual(ScopePalette.Marker(EventKind.Git), ScopePalette.Marker(EventKind.Error));
		Assert.True(ScopePalette.Instance.Count <= 256);
	}
}
