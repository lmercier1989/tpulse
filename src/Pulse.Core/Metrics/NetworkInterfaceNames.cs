namespace Pulse.Core.Metrics;

/// <summary>
/// Which instances of the "Network Interface" counters of another machine carry its real traffic. Virtual switches,
/// team interfaces, tunnels and loopback are left out, because their traffic also crosses a physical adapter and
/// would be counted twice. On this machine Pulse asks the interface itself (<c>GetIfTable2</c>); remotely only the
/// names are known, so this is a name rule.
/// </summary>
public static class NetworkInterfaceNames
{
	private static readonly string[] Virtual =
	[
		"isatap", "teredo", "6to4", "loopback", "pseudo-interface", "vethernet", "hyper-v virtual",
		"multiplexor", "wan miniport", "kernel debug",
	];

	public static bool IsCounted(ReadOnlySpan<char> instance)
	{
		foreach (var marker in Virtual)
		{
			if (instance.Contains(marker, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return !instance.IsWhiteSpace();
	}
}
