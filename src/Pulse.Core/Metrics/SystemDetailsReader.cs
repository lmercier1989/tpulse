using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using Microsoft.Win32;

using Pulse.Core.Diagnostics;
using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>Reads <see cref="SystemDetails"/>. Every part is read on its own, so one failing part leaves the rest.</summary>
internal static class SystemDetailsReader
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

	private static string ReadOperatingSystem()
	{
		try
		{
			using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
			return DescribeWindows(
				key?.GetValue("ProductName") as string,
				key?.GetValue("DisplayVersion") as string,
				key?.GetValue("CurrentBuild") as string,
				key?.GetValue("UBR") is int revision ? revision : null);
		}
		catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			Log.WarnOnce("details-os", "Could not read the Windows version.", ex);
			return Environment.OSVersion.VersionString;
		}
	}

	/// <summary>Fixed and removable volumes that are ready, by drive letter.</summary>
	private static IReadOnlyList<DiskSpace> ReadDisks()
	{
		var disks = new List<DiskSpace>();
		foreach (var drive in DriveInfo.GetDrives())
		{
			if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
			{
				continue;
			}

			disks.Add(new DiskSpace(drive.Name.TrimEnd('\\'), drive.VolumeLabel, drive.TotalSize, drive.TotalFreeSpace));
		}

		disks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		return disks;
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
			&& u.SuffixOrigin != SuffixOrigin.Random);
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

	/// <summary>The battery, or null on a machine without one.</summary>
	private static BatteryStatus? ReadBattery()
	{
		const byte NoBattery = 128;
		const byte Unknown = 255;
		const byte Charging = 8;

		if (!Native.GetSystemPowerStatus(out var status)
			|| status.BatteryFlag is NoBattery or Unknown
			|| status.BatteryLifePercent > 100)
		{
			return null;
		}

		return new BatteryStatus(status.BatteryLifePercent, (status.BatteryFlag & Charging) != 0, status.ACLineStatus == 1);
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

/// <summary>SYSTEM_POWER_STATUS.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
	public byte ACLineStatus;
	public byte BatteryFlag;
	public byte BatteryLifePercent;
	public byte SystemStatusFlag;
	public uint BatteryLifeTime;
	public uint BatteryFullLifeTime;
}
