namespace Pulse.Core.Metrics;

/// <summary>Space on one volume.</summary>
/// <param name="Name">Drive name, for example "C:".</param>
/// <param name="Label">Volume label; may be empty.</param>
/// <param name="TotalBytes">Size of the volume.</param>
/// <param name="FreeBytes">Free space on the volume.</param>
public sealed record DiskSpace(string Name, string Label, double TotalBytes, double FreeBytes)
{
	public double UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

	public double UsedPercent => TotalBytes > 0 ? UsedBytes / TotalBytes * 100 : double.NaN;
}

/// <summary>The addresses of the network connection that carries the default route.</summary>
/// <param name="Adapter">Adapter description.</param>
/// <param name="IPv4">IPv4 address with prefix length, for example "192.168.1.23/24".</param>
/// <param name="Gateway">Default IPv4 gateway.</param>
/// <param name="Dns">First DNS server.</param>
/// <param name="IPv6">First global, non-temporary IPv6 address.</param>
/// <param name="LinkBitsPerSecond">Link speed reported by the adapter; 0 or less when unknown.</param>
public sealed record NetworkDetails(string Adapter, string? IPv4, string? Gateway, string? Dns, string? IPv6, long LinkBitsPerSecond);

/// <summary>Battery charge of a laptop or tablet.</summary>
public sealed record BatteryStatus(double Percent, bool Charging, bool OnPower);

/// <summary>
/// Slowly changing facts about the machine, shown in the info panel. Read every few seconds rather than
/// every sample, because they hardly change and some of the APIs behind them allocate.
/// </summary>
/// <param name="Remote">
/// Another machine, read over the network: its IPv4 address is the one its name resolves to, and the ping goes to it
/// rather than to the gateway.
/// </param>
public sealed record SystemDetails(
	string HostName,
	string OperatingSystem,
	IReadOnlyList<DiskSpace> Disks,
	NetworkDetails? Network,
	BatteryStatus? Battery,
	bool Remote = false);
