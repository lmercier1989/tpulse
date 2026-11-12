using Pulse.Core.CommandLine;
using Pulse.Core.Metrics;
using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.Tests.CommandLine;

/// <summary>The extra graphs (-process, -counter) and another machine (-host) on the command line.</summary>
public sealed class WatchOptionsTests
{
	[Fact]
	public void TryParse_ProcessesAndCounters_KeepTheirOrder()
	{
		// Act
		var parsed = LaunchOptions.TryParse(["-process", "devenv", "-counter", @"\Memory\Available MBytes", "-proc=4120"], out var options, out var error);

		// Assert
		Assert.True(parsed, error);
		Assert.Equal(
			[new WatchSpec(WatchKind.Process, "devenv"), new WatchSpec(WatchKind.Counter, @"\Memory\Available MBytes"), new WatchSpec(WatchKind.Process, "4120")],
			options.Watches);
		Assert.True(options.SelectsGraphs);
		Assert.True(options.AffectsLayout);
	}

	[Fact]
	public void TryParse_Host_StripsLeadingBackslashes()
	{
		// Act
		LaunchOptions.TryParse(["-host", @"\\sql01", "-cpu"], out var options, out _);

		// Assert
		Assert.Equal("sql01", options.Host);
	}

	[Theory]
	[InlineData(new[] { "-process" }, "-process")]
	[InlineData(new[] { "-process", @"C:\tools\x.exe" }, "-process")]
	[InlineData(new[] { "-counter", "Memory" }, "-counter")]
	[InlineData(new[] { "-host" }, "-host")]
	[InlineData(new[] { "-host", "two words" }, "-host")]
	public void TryParse_BadWatchOrHost_ExplainsTheOption(string[] args, string option)
	{
		// Act
		var parsed = LaunchOptions.TryParse(args, out _, out var error);

		// Assert
		Assert.False(parsed);
		Assert.Contains(option, error);
	}

	[Fact]
	public void TryParse_MoreThanThreeExtraGraphs_IsRefused()
	{
		// Act
		var parsed = LaunchOptions.TryParse(["-process", "a", "-process", "b", "-process", "c", "-counter", @"\Memory\Available MBytes"], out _, out var error);

		// Assert
		Assert.False(parsed);
		Assert.Contains("Up to 3", error);
	}

	[Fact]
	public void TryParse_ProcessIdWithHost_IsRefused()
	{
		// Act
		var parsed = LaunchOptions.TryParse(["-host", "sql01", "-process", "4120"], out _, out var error);

		// Assert
		Assert.False(parsed);
		Assert.Contains("4120", error);
	}

	[Fact]
	public void ApplyTo_OnlyAnExtraGraph_HidesTheBuiltInGraphs()
	{
		// Arrange
		LaunchOptions.TryParse(["-process", "devenv"], out var options, out _);
		var settings = new PulseSettings();

		// Act
		options.ApplyTo(settings);

		// Assert
		Assert.All(MetricKinds.Builtin, kind => Assert.False(settings.Channel(kind).Visible));
	}
}
