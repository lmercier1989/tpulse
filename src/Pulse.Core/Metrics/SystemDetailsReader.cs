using System.Net.NetworkInformation;
using System.Net.Sockets;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Metrics;

/// <summary>
/// Reads <see cref="SystemDetails"/>. Every part is read on its own, so one failing part leaves the rest. The operating
/// system, the volumes and the battery come from the platform part (SystemDetailsReader.Windows.cs or .Linux.cs).
/// </summary>
internal static partial class SystemDetailsReader
{
	private static readonly Lazy<string> OperatingSystemName = new(ReadOperatingSystem);

	public static SystemDetails Read() => new(
		Environment.MachineName,
		OperatingSystemName.Value,
		Guard("disks", ReadDisks, []),
		Guard("network-details", ReadNetwork, null),
		Guard("battery", ReadBattery, null));

	/// <summary>
	/// "Windows 11 Enterprise 25H2 (26200.7171)". The registry still says "Windows 10" on Windows 11, so the
	/// build number decides: Windows 11 starts at build 22000.
	/// </summary>
	internal static string DescribeWindows(string? productName, string? displayVersion, string? build, int? revision)
	{
		var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
		if (int.TryParse(build, out var number) && number >= 22000)
		{
			name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
		}

		var version = string.IsNullOrWhiteSpace(displayVersion) ? string.Empty : $" {displayVersion.Trim()}";
		var buildText = string.IsNullOrWhiteSpace(build) ? string.Empty : revision is { } r ? $" ({build}.{r})" : $" ({build})";
		return name + version + buildText;
	}

	/// <summary>The connection with a default gateway (the one traffic leaves through), else the first one with an address.</summary>
	private static NetworkDetails? ReadNetwork()
	{
		NetworkInterface? best = null;
		IPInterfaceProperties? bestProperties = null;
		var bestScore = 0;
		foreach (var candidate in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (candidate.OperationalStatus != OperationalStatus.Up
				|| candidate.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
			{
				continue;
			}

			var properties = candidate.GetIPProperties();
			var score = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)) ? 2
				: properties.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork) ? 1
				: 0;
			if (score > bestScore || (score == bestScore && score > 0 && candidate.Speed > (best?.Speed ?? 0)))
			{
				best = candidate;
				bestProperties = properties;
				bestScore = score;
			}
		}

		if (best is null || bestProperties is null)
		{
			return null;
		}

		var ipv4 = bestProperties.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
		var ipv6 = bestProperties.UnicastAddresses.FirstOrDefault(u =>
			u.Address.AddressFamily == AddressFamily.InterNetworkV6
			&& !u.Address.IsIPv6LinkLocal
			&& !u.Address.IsIPv6SiteLocal
			&& (!OperatingSystem.IsWindows() || u.SuffixOrigin != SuffixOrigin.Random));
		var gateway = bestProperties.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
		var dns = bestProperties.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
				  ?? bestProperties.DnsAddresses.FirstOrDefault();

		return new NetworkDetails(
			best.Description,
			ipv4 is null ? null : $"{ipv4.Address}/{ipv4.PrefixLength}",
			gateway?.Address.ToString(),
			dns?.ToString(),
			ipv6?.Address.ToString(),
			best.Speed);
	}

	private static T Guard<T>(string part, Func<T> read, T fallback)
	{
		try
		{
			return read();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Log.WarnOnce($"details-{part}", $"Reading '{part}' for the info panel failed; it is left out.", ex);
			return fallback;
		}
	}
}
