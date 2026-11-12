using System.Globalization;
using System.Text.RegularExpressions;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Interop;

/// <summary>
/// The GPU on Linux: an AMD (or other DRM) card that reports its load in <c>gpu_busy_percent</c>, with memory, temperature
/// and power from sysfs; or an NVIDIA card through NVML. With more than one, the card with the most dedicated memory is
/// the discrete GPU of a hybrid laptop. Intel graphics report no load in sysfs and are not shown.
/// </summary>
internal sealed partial class LinuxGpu : IDisposable
{
	private const uint AmdVendorId = 0x1002;
	private const uint NvidiaVendorId = 0x10DE;
	private const uint IntelVendorId = 0x8086;

	private readonly string? _device;
	private readonly string? _hwmon;
	private readonly NvmlGpu? _nvml;

	private LinuxGpu(string name, double dedicatedBytes, string? device, string? hwmon, NvmlGpu? nvml)
	{
		Name = name;
		DedicatedBytes = dedicatedBytes;
		_device = device;
		_hwmon = hwmon;
		_nvml = nvml;
	}

	public string Name { get; }

	/// <summary>Dedicated video memory in bytes; NaN when unknown.</summary>
	public double DedicatedBytes { get; }

	/// <summary>The GPU with the most dedicated memory that reports its load, or null.</summary>
	public static LinuxGpu? TryOpen()
	{
		const string Root = "/sys/class/drm";
		if (!Directory.Exists(Root))
		{
			return null;
		}

		LinuxGpu? best = null;
		foreach (var card in Directory.EnumerateDirectories(Root).Where(path => CardName().IsMatch(Path.GetFileName(path))).Order(StringComparer.Ordinal))
		{
			var device = Path.Combine(card, "device");
			var vendor = LinuxProc.HexId(Text(device, "vendor"));
			var id = LinuxProc.HexId(Text(device, "device"));
			var name = PciName(vendor, id, LinuxProc.HexId(Text(device, "subsystem_vendor")), LinuxProc.HexId(Text(device, "subsystem_device")));
			LinuxGpu? candidate = null;
			if (vendor == NvidiaVendorId && NvmlGpu.TryOpen(vendor, id) is { } nvml)
			{
				candidate = new LinuxGpu(nvml.Name ?? name, nvml.ReadMemory().Total, null, null, nvml);
			}
			else if (File.Exists(Path.Combine(device, "gpu_busy_percent")))
			{
				var vram = Number(device, "mem_info_vram_total");
				var hwmon = Directory.Exists(Path.Combine(device, "hwmon"))
					? Directory.EnumerateDirectories(Path.Combine(device, "hwmon")).Order(StringComparer.Ordinal).FirstOrDefault()
					: null;
				candidate = new LinuxGpu(name, vram, device, hwmon, null);
			}

			Log.Info($"GPU {Path.GetFileName(card)}: {name} (vendor 0x{vendor:X4}, device 0x{id:X4}){(candidate is null ? ", reports no load" : string.Empty)}");
			if (candidate is null)
			{
				continue;
			}

			if (best is null || (candidate.DedicatedBytes > best.DedicatedBytes) || (double.IsNaN(best.DedicatedBytes) && !double.IsNaN(candidate.DedicatedBytes)))
			{
				best?.Dispose();
				best = candidate;
			}
			else
			{
				candidate.Dispose();
			}
		}

		Log.Info(best is null ? "No GPU that reports its load was found." : $"GPU selected: {best.Name}");
		return best;
	}

	/// <summary>The load in %, NaN when unknown.</summary>
	public double ReadUtilizationPercent() => _nvml?.ReadUtilizationPercent() ?? Number(_device, "gpu_busy_percent");

	/// <summary>Dedicated memory in use, in bytes; NaN when unknown.</summary>
	public double ReadMemoryUsedBytes() => _nvml?.ReadMemory().Used ?? Number(_device, "mem_info_vram_used");

	/// <summary>The edge temperature (the one AMD's own tools show) in degrees Celsius; NaN when unknown.</summary>
	public double ReadTemperatureCelsius()
		=> _nvml?.ReadTemperatureCelsius() ?? Number(_hwmon, TemperatureFile()) / 1000;

	/// <summary>Board power in watts; NaN when unknown.</summary>
	public double ReadPowerWatts()
	{
		if (_nvml is not null)
		{
			return _nvml.ReadPowerWatts();
		}

		var microwatts = Number(_hwmon, "power1_average");
		return (double.IsNaN(microwatts) ? Number(_hwmon, "power1_input") : microwatts) / 1_000_000;
	}

	public void Dispose() => _nvml?.Dispose();

	/// <summary>"AMD Radeon RX 9060 XT" from the system's pci.ids; the vendor and the id when the device is not listed.</summary>
	private static string PciName(uint vendor, uint device, uint subVendor, uint subDevice)
	{
		var brand = vendor switch
		{
			AmdVendorId => "AMD",
			NvidiaVendorId => "NVIDIA",
			IntelVendorId => "Intel",
			_ => $"GPU 0x{vendor:X4}",
		};

		foreach (var database in new[] { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids" })
		{
			try
			{
				if (File.Exists(database) && LinuxProc.PciDeviceName(File.ReadLines(database), vendor, device, subVendor, subDevice) is { } name)
				{
					return name.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? name : $"{brand} {name}";
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Log.WarnOnce("gpu-pci-ids", $"Could not read {database}.", ex);
			}
		}

		return $"{brand} 0x{device:X4}";
	}

	private string TemperatureFile()
	{
		if (_hwmon is null)
		{
			return "temp1_input";
		}

		for (var i = 1; i <= 4; i++)
		{
			if (Text(_hwmon, $"temp{i}_label") == "edge")
			{
				return $"temp{i}_input";
			}
		}

		return "temp1_input";
	}

	private static string? Text(string? folder, string file)
	{
		if (folder is null)
		{
			return null;
		}

		try
		{
			return File.ReadAllText(Path.Combine(folder, file)).Trim();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static double Number(string? folder, string file)
		=> double.TryParse(Text(folder, file), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

	[GeneratedRegex(@"^card\d+$")]
	private static partial Regex CardName();
}
