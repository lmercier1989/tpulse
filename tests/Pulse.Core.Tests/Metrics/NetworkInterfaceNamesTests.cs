using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class NetworkInterfaceNamesTests
{
	[Theory]
	[InlineData("Intel[R] Ethernet Controller X710", true)]
	[InlineData("Realtek Gaming 2.5GbE Family Controller", true)]
	[InlineData("vEthernet (Default Switch)", false)]
	[InlineData("Hyper-V Virtual Ethernet Adapter _2", false)]
	[InlineData("Microsoft Network Adapter Multiplexor Driver", false)]
	[InlineData("isatap.{5A1E}", false)]
	[InlineData("Teredo Tunneling Pseudo-Interface", false)]
	[InlineData("Software Loopback Interface 1", false)]
	[InlineData("", false)]
	public void IsCounted_OnlyPhysicalAdapters(string name, bool expected)
	{
		// Act & Assert
		Assert.Equal(expected, NetworkInterfaceNames.IsCounted(name));
	}
}
