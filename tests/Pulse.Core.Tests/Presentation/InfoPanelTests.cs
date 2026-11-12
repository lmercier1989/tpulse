using Pulse.Core.Metrics;
using Pulse.Core.Presentation;

namespace Pulse.Core.Tests.Presentation;

public sealed class InfoPanelTests
{
	private static readonly SystemDetails Details = new(
		"WORKSTATION",
		"Windows 11 Pro 25H2 (26200.100)",
		[
			new DiskSpace("C:", "Windows", 1000e9, 400e9),
			new DiskSpace("D:", "Data", 2000e9, 100e9),
		],
		new NetworkDetails("Realtek 2.5GbE", "192.168.1.23/24", "192.168.1.1", "192.168.1.1", "2001:db8::23", 2_500_000_000),
		Battery: null);

	private static readonly MetricSnapshot Sample = new() { Time = 1, ProcessCount = 312, ThreadCount = 4812 };

	[Fact]
	public void Build_AllDetails_HasTheThreeSectionsInOrder()
	{
		// Act
		var lines = InfoPanel.Build(Details, Sample, TimeSpan.FromHours(5));

		// Assert
		var sections = lines.Where(line => line.Kind == PanelLineKind.Section).Select(line => line.Label).ToArray();
		Assert.Equal(new[] { "SYSTEM", "NETWORK", "DISKS" }, sections);
		Assert.Contains(lines, line => line is { Label: "ipv4", Text: "192.168.1.23/24" });
		Assert.Contains(lines, line => line.Kind == PanelLineKind.Gauge && line.Label == "D:" && Math.Abs(line.Percent - 95) < 1e-9);
	}

	[Fact]
	public void Build_ClaudeRunning_ShowsItsLoadAndActiveSessions()
	{
		// Arrange
		var sample = new MetricSnapshot
		{
			Time = 1,
			PingMs = 0.4,
			ClaudeProcesses = new ClaudeProcessUsage(1.5, 0.5, 12, 600e6, 300e6, 900e6, 2, 5),
			ClaudeSessions = [new ClaudeSession("s1", "Pulse", "main", "claude-opus-5-5", 142_000, TimeProvider.System.GetUtcNow(), default)],
		};

		// Act
		var lines = InfoPanel.Build(Details, sample, TimeSpan.FromHours(1));

		// Assert
		Assert.Contains(lines, line => line.Label == "CLAUDE");
		Assert.Contains(lines, line => line is { Label: "sessions", Text: "2" } && line.Right.EndsWith("MB", StringComparison.Ordinal));
		Assert.Contains(lines, line => line is { Label: "tools", Text: "5" } && line.Right.Length > 0);
		Assert.Contains(lines, line => line is { Kind: PanelLineKind.Field, Label: "Pulse", Text: "142k", Right: "just now" });
		Assert.Contains(lines, line => line is { Label: "ping", Text: "<1 ms" });
	}

	[Theory]
	[InlineData(2, "now")]
	[InlineData(45, "45 s")]
	[InlineData(200, "3 min")]
	public void FormatAgo_ReadsNaturally(int seconds, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, InfoPanel.FormatAgo(TimeSpan.FromSeconds(seconds)));
	}

	[Fact]
	public void Build_BeforeDetailsArrive_StillShowsUptime()
	{
		// Act
		var lines = InfoPanel.Build(details: null, sample: null, TimeSpan.FromMinutes(7));

		// Assert
		Assert.Contains(lines, line => line is { Label: "uptime", Text: "7 min" });
		Assert.DoesNotContain(lines, line => line.Label == "NETWORK");
	}

	[Fact]
	public void Fit_EnoughRows_KeepsEverything()
	{
		// Arrange
		var lines = InfoPanel.Build(Details, Sample, TimeSpan.FromHours(5));

		// Act
		var fitted = InfoPanel.Fit(lines, 100);

		// Assert
		Assert.Equal(lines, fitted);
	}

	[Fact]
	public void Fit_FewerRows_DropsTheLeastImportantFirst()
	{
		// Arrange
		var lines = InfoPanel.Build(Details, Sample, TimeSpan.FromHours(5));

		// Act
		var fitted = InfoPanel.Fit(lines, lines.Count - 4);

		// Assert: the blank separators and the IPv6 address go before the host, the IPv4 address and the gauges.
		Assert.Equal(lines.Count - 4, fitted.Count);
		Assert.DoesNotContain(fitted, line => line.Kind == PanelLineKind.Blank);
		Assert.DoesNotContain(fitted, line => line.Label == "ipv6");
		Assert.Contains(fitted, line => line.Label == "host");
		Assert.Contains(fitted, line => line.Label == "ipv4");
		Assert.Equal(2, fitted.Count(line => line.Kind == PanelLineKind.Gauge));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(3)]
	[InlineData(6)]
	[InlineData(10)]
	public void Fit_AnyHeight_NeverExceedsItAndLeavesNoEmptySection(int rows)
	{
		// Arrange
		var lines = InfoPanel.Build(Details, Sample, TimeSpan.FromHours(5));

		// Act
		var fitted = InfoPanel.Fit(lines, rows);

		// Assert
		Assert.True(fitted.Count <= rows);
		for (var i = 0; i < fitted.Count; i++)
		{
			if (fitted[i].Kind == PanelLineKind.Section)
			{
				Assert.True(i + 1 < fitted.Count && fitted[i + 1].Kind is not (PanelLineKind.Section or PanelLineKind.Blank));
			}
		}
	}

	[Fact]
	public void Fit_NoRows_IsEmpty()
	{
		// Act & Assert
		Assert.Empty(InfoPanel.Fit(InfoPanel.Build(Details, Sample, TimeSpan.Zero), 0));
	}

	[Theory]
	[InlineData(0, "0 min")]
	[InlineData(59, "59 min")]
	[InlineData(61, "1 h 1 min")]
	[InlineData(60 * 24 * 3 + 125, "3 d 2 h")]
	public void FormatUptime_ReadsNaturally(int minutes, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, InfoPanel.FormatUptime(TimeSpan.FromMinutes(minutes)));
	}

	[Theory]
	[InlineData("Windows 10 Pro", "25H2", "26200", 7171, "Windows 11 Pro 25H2 (26200.7171)")]
	[InlineData("Windows 10 Enterprise", "22H2", "19045", 5000, "Windows 10 Enterprise 22H2 (19045.5000)")]
	[InlineData(null, null, null, null, "Windows")]
	public void DescribeWindows_UsesTheBuildToNameWindows11(string? product, string? version, string? build, int? revision, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, SystemDetailsReader.DescribeWindows(product, version, build, revision));
	}

	[Fact]
	public void DiskSpace_ComputesUsedShare()
	{
		// Arrange
		var disk = new DiskSpace("C:", string.Empty, 1000, 250);

		// Act & Assert
		Assert.Equal(750, disk.UsedBytes);
		Assert.Equal(75, disk.UsedPercent);
	}

	[Fact]
	public void Build_ListeningPorts_ARowEachThatOpensItsMenu()
	{
		// Arrange
		var sample = new MetricSnapshot
		{
			Time = 1,
			Ports = new PortActivity([new ListeningPort(5000, 4242, "dotnet", LocalOnly: true), new ListeningPort(8080, 77, "node", LocalOnly: false)], SystemPorts: 9),
		};

		// Act
		var lines = InfoPanel.Build(Details, sample, TimeSpan.FromHours(1));

		// Assert
		Assert.Contains(lines, line => line is { Kind: PanelLineKind.Section, Label: "PORTS", Text: "2 listening" });
		var dotnet = Assert.Single(lines, line => line.Label == "5000");
		Assert.Equal(("dotnet", "localhost"), (dotnet.Text, dotnet.Right));
		Assert.Equal(new PanelLink(PanelLinkKind.Port, "dotnet", Port: 5000, ProcessId: 4242), dotnet.Link);
		Assert.Contains(lines, line => line is { Label: "8080", Right: "network" });
		Assert.Contains(lines, line => line.Kind == PanelLineKind.Note && line.Text.StartsWith("9 more held by", StringComparison.Ordinal));
	}

	[Fact]
	public void Build_ManyPorts_ListsTheFirstAndCountsTheRest()
	{
		// Arrange
		var ports = Enumerable.Range(0, InfoPanel.MaximumPorts + 2).Select(i => new ListeningPort(3000 + i, 100 + i, "node", true)).ToList();
		var sample = new MetricSnapshot { Time = 1, Ports = new PortActivity(ports, 0) };

		// Act
		var lines = InfoPanel.Build(Details, sample, TimeSpan.FromHours(1));

		// Assert
		Assert.Equal(InfoPanel.MaximumPorts, lines.Count(line => line.Link?.Kind == PanelLinkKind.Port));
		Assert.Contains(lines, line => line is { Kind: PanelLineKind.Note, Text: "2 more" });
	}

	[Fact]
	public void Build_NoPortsKnown_HasNoPortsSection()
		=> Assert.DoesNotContain(InfoPanel.Build(Details, Sample, TimeSpan.FromHours(1)), line => line.Label == "PORTS");
}
