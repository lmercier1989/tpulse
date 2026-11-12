using Pulse.Core.Diagnostics;
using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>The Linux part of <see cref="SystemDetailsReader"/>: /etc/os-release, the mounted volumes and /sys power supplies.</summary>
internal static partial class SystemDetailsReader
{
	/// <summary>"CachyOS Linux (6.17.3-1-cachyos)": the distribution and the kernel release.</summary>
	private static string ReadOperatingSystem()
	{
		string? name = null;
		foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
		{
			try
			{
				name = LinuxProc.OsReleaseName(File.ReadAllText(path));
				break;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Try the next place; every distribution with systemd has one of them.
			}
		}

		string? kernel = null;
		try
		{
			kernel = File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.WarnOnce("details-kernel", "Could not read the kernel release.", ex);
		}

		return (name ?? "Linux") + (string.IsNullOrEmpty(kernel) ? string.Empty : $" ({kernel})");
	}

	/// <summary>
	/// The volumes on a block device, by mount point. A file system mounted more than once (btrfs subvolumes, bind mounts)
	/// counts once, under its shortest mount point; boot and EFI partitions are left out.
	/// </summary>
	private static IReadOnlyList<DiskSpace> ReadDisks()
	{
		var byDevice = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (device, mountPoint, _) in LinuxProc.ParseMounts(File.ReadAllText("/proc/mounts")))
		{
			if (mountPoint is "/boot" or "/efi" || mountPoint.StartsWith("/boot/", StringComparison.Ordinal))
			{
				continue;
			}

			if (!byDevice.TryGetValue(device, out var known) || mountPoint.Length < known.Length)
			{
				byDevice[device] = mountPoint;
			}
		}

		var disks = new List<DiskSpace>();
		foreach (var (device, mountPoint) in byDevice)
		{
			try
			{
				var drive = new DriveInfo(mountPoint);
				if (drive.IsReady && drive.TotalSize > 0)
				{
					disks.Add(new DiskSpace(mountPoint, Path.GetFileName(device), drive.TotalSize, drive.AvailableFreeSpace));
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
				// Unmounted meanwhile, or a mount point this user may not look into.
			}
		}

		disks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		return disks;
	}

	/// <summary>
	/// The system battery of <c>/sys/class/power_supply</c>, or null on a machine without one. The batteries of a mouse or a
	/// headset are power supplies too, with the scope "Device"; they are left out.
	/// </summary>
	private static BatteryStatus? ReadBattery()
	{
		const string Root = "/sys/class/power_supply";
		if (!Directory.Exists(Root))
		{
			return null;
		}

		double capacity = 0, full = 0;
		var batteries = 0;
		var charging = false;
		var onPower = false;
		foreach (var supply in Directory.EnumerateDirectories(Root))
		{
			var type = Text(supply, "type");
			if (type == "Mains" || (type == "USB" && Text(supply, "online") == "1"))
			{
				onPower |= Text(supply, "online") == "1";
				continue;
			}

			if (type != "Battery" || Text(supply, "scope") == "Device" || Text(supply, "present") == "0")
			{
				continue;
			}

			if (!double.TryParse(Text(supply, "capacity"), System.Globalization.CultureInfo.InvariantCulture, out var percent))
			{
				continue;
			}

			// Two batteries (some ThinkPads) are averaged by their size when it is known.
			var size = double.TryParse(Text(supply, "energy_full") ?? Text(supply, "charge_full"), System.Globalization.CultureInfo.InvariantCulture, out var energy) && energy > 0 ? energy : 1;
			capacity += percent * size;
			full += size;
			batteries++;
			var status = Text(supply, "status");
			charging |= status == "Charging";
			onPower |= status is "Charging" or "Full" or "Not charging";
		}

		return batteries == 0 || full <= 0 ? null : new BatteryStatus(Math.Clamp(capacity / full, 0, 100), charging, onPower);

		static string? Text(string folder, string file)
		{
			try
			{
				return File.ReadAllText(Path.Combine(folder, file)).Trim();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}
	}
}
