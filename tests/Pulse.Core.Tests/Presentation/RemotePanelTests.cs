using Pulse.Core.Metrics;
using Pulse.Core.Presentation;

namespace Pulse.Core.Tests.Presentation;

/// <summary>The info panel of a machine read with -host.</summary>
public sealed class RemotePanelTests
{
	private static readonly SystemDetails Remote = new(
		"sql01",
		string.Empty,
		[new DiskSpace("C:", string.Empty, 200e9, 50e9)],
		new NetworkDetails("Intel[R] Ethernet X710", "10.1.2.30", null, null, null, 10_000_000_000),
		null,
		Remote: true);

	[Fact]
	public void Build_RemoteMachine_MarksItAndPingsIt()
	{
		// Arrange
		var sample = new MetricSnapshot { Time = 1, PingMs = 3.2 };

		// Act
		var lines = InfoPanel.Build(Remote, sample, TimeSpan.FromDays(41));

		// Assert
		Assert.Contains(lines, line => line is { Label: "host", Text: "sql01", Right: "remote" });
		Assert.Contains(lines, line => line is { Label: "ping", Text: "3 ms" });
		Assert.Contains(lines, line => line is { Label: "ipv4", Text: "10.1.2.30" });
		Assert.DoesNotContain(lines, line => line.Label is "windows" or "gateway");
	}

	[Fact]
	public void Build_UptimeUnknown_LeavesTheRowOut()
	{
		// Act
		var lines = InfoPanel.Build(Remote, sample: null, uptime: null);

		// Assert
		Assert.DoesNotContain(lines, line => line.Label == "uptime");
	}
}
