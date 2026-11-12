using Pulse.Core.CommandLine;
using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.Tests.CommandLine;

public sealed class LaunchOptionsTests
{
	[Fact]
	public void TryParse_NoArguments_SelectsNothing()
	{
		// Act
		var parsed = LaunchOptions.TryParse([], out var options, out var error);

		// Assert
		Assert.True(parsed);
		Assert.Null(error);
		Assert.Null(options.Metrics);
		Assert.False(options.AffectsLayout);
	}

	[Fact]
	public void TryParse_CpuAndMemory_SelectsBoth()
	{
		// Act
		LaunchOptions.TryParse(["-cpu", "-memory"], out var options, out _);

		// Assert
		Assert.Equal(new[] { MetricKind.Cpu, MetricKind.Memory }, options.Metrics!.Order());
		Assert.True(options.AffectsLayout);
	}

	[Fact]
	public void TryParse_All_SelectsEveryGraph()
	{
		// Act
		LaunchOptions.TryParse(["-all"], out var options, out _);

		// Assert
		Assert.Equal(MetricKinds.Builtin, options.Metrics!.Order());
	}

	[Fact]
	public void TryParse_AliasesPrefixesAndCase_AreAccepted()
	{
		// Act
		var parsed = LaunchOptions.TryParse(["--net", "/gpu", "-MEM", "disk"], out var options, out _);

		// Assert
		Assert.True(parsed);
		Assert.Equal(new[] { MetricKind.Gpu, MetricKind.Memory, MetricKind.Disk, MetricKind.Network }, options.Metrics!.Order());
	}

	[Theory]
	[InlineData(new[] { "-history", "5m" }, 300.0)]
	[InlineData(new[] { "-history=30s" }, 30.0)]
	[InlineData(new[] { "--hist:90" }, 90.0)]
	public void TryParse_History_AcceptsSeparateAndInlineValues(string[] args, double expected)
	{
		// Act
		LaunchOptions.TryParse(args, out var options, out _);

		// Assert
		Assert.Equal(expected, options.HistorySeconds);
	}

	[Fact]
	public void TryParse_IntervalFpsAndSwitches_AreRead()
	{
		// Act
		var parsed = LaunchOptions.TryParse(["-interval", "500ms", "-fps", "30", "-cores", "-compact", "-top"], out var options, out _);

		// Assert
		Assert.True(parsed);
		Assert.Equal(0.5, options.IntervalSeconds);
		Assert.Equal(30, options.Fps);
		Assert.True(options.Cores);
		Assert.True(options.Compact);
		Assert.True(options.Top);
		Assert.True(options.AffectsLayout);
	}

	[Theory]
	[InlineData("-sixel", TerminalRenderer.Sixel)]
	[InlineData("-braille", TerminalRenderer.Braille)]
	[InlineData("--text", TerminalRenderer.Braille)]
	public void TryParse_Renderer_IsChosen(string argument, TerminalRenderer expected)
	{
		// Act
		LaunchOptions.TryParse([argument], out var options, out _);

		// Assert
		Assert.Equal(expected, options.Renderer);
	}

	[Fact]
	public void TryParse_Window_RequestsTheWindowButNotTheHost()
	{
		// Act
		LaunchOptions.TryParse(["-cpu", "-window"], out var window, out _);
		LaunchOptions.TryParse(["-cpu", "--window-host"], out var host, out _);

		// Assert
		Assert.True(window.Window);
		Assert.False(window.WindowHost);
		Assert.True(host.WindowHost);
		Assert.Equal(TerminalRenderer.Auto, window.Renderer);
	}

	[Theory]
	[InlineData(new[] { "-record" }, "")]
	[InlineData(new[] { "-record", "-cpu" }, "")]
	[InlineData(new[] { "-record", "C:\\runs\\build.csv" }, "C:\\runs\\build.csv")]
	[InlineData(new[] { "-rec=run.csv" }, "run.csv")]
	public void TryParse_Record_TakesAnOptionalFile(string[] args, string expected)
	{
		// Act
		LaunchOptions.TryParse(args, out var options, out _);

		// Assert
		Assert.Equal(expected, options.RecordPath);
	}

	[Fact]
	public void TryParse_Claude_SelectsTheClaudeGraph()
	{
		// Act
		LaunchOptions.TryParse(["-cpu", "-claude"], out var options, out _);

		// Assert
		Assert.Equal(new[] { MetricKind.Cpu, MetricKind.Claude }, options.Metrics!.Order());
		Assert.Null(options.RecordPath);
	}

	[Theory]
	[InlineData("-here")]
	[InlineData("--inline")]
	public void TryParse_Here_DrawsInTheCurrentTab(string argument)
	{
		// Act
		LaunchOptions.TryParse(["-cpu", argument], out var options, out _);

		// Assert
		Assert.True(options.Here);
		Assert.False(LaunchOptions.TryParse(["-cpu"], out var defaults, out _) && defaults.Here);
	}

	[Theory]
	[InlineData("-h")]
	[InlineData("-?")]
	[InlineData("/?")]
	[InlineData("--help")]
	public void TryParse_HelpSpellings_RequestHelp(string argument)
	{
		// Act
		LaunchOptions.TryParse([argument], out var options, out _);

		// Assert
		Assert.True(options.ShowHelp);
	}

	[Theory]
	[InlineData(new[] { "-cpu", "-bogus" }, "-bogus")]
	[InlineData(new[] { "-history" }, "-history")]
	[InlineData(new[] { "-history", "-cpu" }, "-history")]
	[InlineData(new[] { "-history", "5s" }, "-history")]
	[InlineData(new[] { "-interval", "10s" }, "-interval")]
	[InlineData(new[] { "-fps", "500" }, "-fps")]
	public void TryParse_InvalidInput_FailsNamingTheOption(string[] args, string option)
	{
		// Act
		var parsed = LaunchOptions.TryParse(args, out _, out var error);

		// Assert
		Assert.False(parsed);
		Assert.Contains(option, error);
	}

	[Fact]
	public void ApplyTo_WithGraphs_ShowsOnlyThoseGraphs()
	{
		// Arrange
		LaunchOptions.TryParse(["-cpu", "-network", "-history", "5m"], out var options, out _);
		var settings = new PulseSettings();

		// Act
		options.ApplyTo(settings);

		// Assert
		Assert.True(settings.Channel(MetricKind.Cpu).Visible);
		Assert.True(settings.Channel(MetricKind.Network).Visible);
		Assert.False(settings.Channel(MetricKind.Gpu).Visible);
		Assert.False(settings.Channel(MetricKind.Memory).Visible);
		Assert.False(settings.Channel(MetricKind.Disk).Visible);
		Assert.Equal(300, settings.HistorySeconds);
	}

	[Fact]
	public void ApplyTo_WithoutGraphs_KeepsRememberedVisibility()
	{
		// Arrange
		LaunchOptions.TryParse(["-top"], out var options, out _);
		var settings = new PulseSettings();
		settings.Channel(MetricKind.Disk).Visible = false;

		// Act
		options.ApplyTo(settings);

		// Assert
		Assert.False(settings.Channel(MetricKind.Disk).Visible);
		Assert.True(settings.Channel(MetricKind.Cpu).Visible);
		Assert.True(settings.AlwaysOnTop);
	}
}
