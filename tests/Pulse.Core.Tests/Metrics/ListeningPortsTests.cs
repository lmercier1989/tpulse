using Pulse.Core.Interop;
using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>The listening sockets grouped into the ports of programs, and the ones of the system counted.</summary>
public sealed class ListeningPortsTests
{
	private static readonly Dictionary<int, (string Name, bool System)> Owners = new()
	{
		[4] = ("System", true),
		[10] = ("dotnet", false),
		[11] = ("node", false),
		[12] = ("devenv", false),
	};

	[Fact]
	public void Group_PortOverIpv4AndIpv6_IsOneRow_LocalOnlyWhenBothAre()
	{
		// Arrange
		TcpListener[] listeners = [new(5000, 10, LocalOnly: true), new(5000, 10, LocalOnly: false), new(7000, 11, true), new(7000, 11, true)];

		// Act
		var ports = ListeningPorts.Group(listeners, Owner).Ports;

		// Assert
		Assert.Equal([new ListeningPort(5000, 10, "dotnet", false), new ListeningPort(7000, 11, "node", true)], ports);
	}

	[Fact]
	public void Group_SystemAndUnknownProcesses_AreOnlyCounted()
	{
		// Arrange
		TcpListener[] listeners = [new(135, 4, false), new(445, 0, false), new(3000, 99, true), new(8080, 11, false)];

		// Act
		var activity = ListeningPorts.Group(listeners, Owner);

		// Assert
		Assert.Equal(8080, Assert.Single(activity.Ports).Port);
		Assert.Equal(3, activity.SystemPorts);
	}

	[Fact]
	public void Group_PortsTheSystemHandsOut_ComeAfterTheChosenOnes()
	{
		// Arrange
		TcpListener[] listeners = [new(60123, 12, true), new(8080, 11, false), new(1433, 10, false)];

		// Act
		var ports = ListeningPorts.Group(listeners, Owner).Ports.Select(port => port.Port);

		// Assert
		Assert.Equal([1433, 8080, 60123], ports);
	}

	[Fact]
	public void Url_IsLocalhostWithThePort()
		=> Assert.Equal("http://localhost:5173", new ListeningPort(5173, 1, "node", true).Url);

	private static (string Name, bool System)? Owner(int id) => Owners.TryGetValue(id, out var owner) ? owner : null;
}
