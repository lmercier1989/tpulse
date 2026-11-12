using System.Runtime.InteropServices;

using Microsoft.Win32;

using Pulse.Core.Diagnostics;
using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>The Windows part of <see cref="SystemDetailsReader"/>: the registry, drive letters and the power status.</summary>
internal static partial class SystemDetailsReader
{
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
