using System.Runtime.InteropServices;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Interop;

/// <summary>
/// Board power (and a temperature fallback) for NVIDIA GPUs through NVML, which the NVIDIA driver installs
/// as nvml.dll. The library is loaded dynamically, so Pulse runs unchanged on systems without it.
/// </summary>
internal sealed unsafe class NvmlGpu : IDisposable
{
	private const uint NvidiaVendorId = 0x10DE;
	private const int Success = 0;
	private const int GpuTemperatureSensor = 0;

	private readonly nint _library;
	private readonly nint _device;
	private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getPowerUsage;
	private readonly delegate* unmanaged[Cdecl]<nint, int, uint*, int> _getTemperature;
	private readonly delegate* unmanaged[Cdecl]<int> _shutdown;
	private bool _disposed;

	private NvmlGpu(
		nint library,
		nint device,
		delegate* unmanaged[Cdecl]<nint, uint*, int> getPowerUsage,
		delegate* unmanaged[Cdecl]<nint, int, uint*, int> getTemperature,
		delegate* unmanaged[Cdecl]<int> shutdown)
	{
		_library = library;
		_device = device;
		_getPowerUsage = getPowerUsage;
		_getTemperature = getTemperature;
		_shutdown = shutdown;
	}

	/// <summary>Opens the NVML device that matches the given PCI ids, or returns null.</summary>
	public static NvmlGpu? TryOpen(uint vendorId, uint deviceId)
	{
		if (vendorId != NvidiaVendorId || !TryLoadLibrary(out var library))
		{
			return null;
		}

		try
		{
			if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init)
				|| !NativeLibrary.TryGetExport(library, "nvmlShutdown", out var shutdown)
				|| !NativeLibrary.TryGetExport(library, "nvmlDeviceGetCount_v2", out var getCount)
				|| !NativeLibrary.TryGetExport(library, "nvmlDeviceGetHandleByIndex_v2", out var getHandle)
				|| !NativeLibrary.TryGetExport(library, "nvmlDeviceGetPowerUsage", out var getPower)
				|| !NativeLibrary.TryGetExport(library, "nvmlDeviceGetTemperature", out var getTemperature))
			{
				Log.Info("nvml.dll found but it lacks the expected exports; GPU power is unavailable.");
				NativeLibrary.Free(library);
				return null;
			}

			var initResult = ((delegate* unmanaged[Cdecl]<int>) init)();
			if (initResult != Success)
			{
				Log.Info($"nvmlInit_v2 failed ({initResult}); GPU power is unavailable.");
				NativeLibrary.Free(library);
				return null;
			}

			var device = FindDevice(library, getCount, getHandle, vendorId, deviceId);
			if (device == 0)
			{
				Log.Info("NVML has no device matching the selected adapter; GPU power is unavailable.");
				_ = ((delegate* unmanaged[Cdecl]<int>) shutdown)();
				NativeLibrary.Free(library);
				return null;
			}

			Log.Info("NVML opened for GPU power readings.");
			return new NvmlGpu(
				library,
				device,
				(delegate* unmanaged[Cdecl]<nint, uint*, int>) getPower,
				(delegate* unmanaged[Cdecl]<nint, int, uint*, int>) getTemperature,
				(delegate* unmanaged[Cdecl]<int>) shutdown);
		}
		catch (Exception ex)
		{
			Log.Warn("NVML initialisation failed; GPU power is unavailable.", ex);
			NativeLibrary.Free(library);
			return null;
		}
	}

	/// <summary>Board power draw in watts, or NaN when the board does not report it.</summary>
	public double ReadPowerWatts()
	{
		if (_disposed)
		{
			return double.NaN;
		}

		uint milliwatts;
		return _getPowerUsage(_device, &milliwatts) == Success ? milliwatts / 1000.0 : double.NaN;
	}

	/// <summary>GPU core temperature in degrees Celsius, or NaN.</summary>
	public double ReadTemperatureCelsius()
	{
		if (_disposed)
		{
			return double.NaN;
		}

		uint celsius;
		return _getTemperature(_device, GpuTemperatureSensor, &celsius) == Success ? celsius : double.NaN;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_ = _shutdown();
		NativeLibrary.Free(_library);
	}

	private static bool TryLoadLibrary(out nint library)
	{
		if (NativeLibrary.TryLoad("nvml.dll", typeof(NvmlGpu).Assembly, DllImportSearchPath.System32, out library))
		{
			return true;
		}

		// Drivers before R418 installed NVML next to nvidia-smi instead of System32.
		var legacy = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			"NVIDIA Corporation",
			"NVSMI",
			"nvml.dll");
		return File.Exists(legacy) && NativeLibrary.TryLoad(legacy, out library);
	}

	private static nint FindDevice(nint library, nint getCount, nint getHandle, uint vendorId, uint deviceId)
	{
		uint count;
		if (((delegate* unmanaged[Cdecl]<uint*, int>) getCount)(&count) != Success || count == 0)
		{
			return 0;
		}

		var handleByIndex = (delegate* unmanaged[Cdecl]<uint, nint*, int>) getHandle;
		var hasPciInfo = NativeLibrary.TryGetExport(library, "nvmlDeviceGetPciInfo_v3", out var getPci)
			|| NativeLibrary.TryGetExport(library, "nvmlDeviceGetPciInfo_v2", out getPci);
		var expected = (deviceId << 16) | vendorId;
		nint first = 0;

		for (uint index = 0; index < count; index++)
		{
			nint device;
			if (handleByIndex(index, &device) != Success)
			{
				continue;
			}

			if (first == 0)
			{
				first = device;
			}

			NvmlPciInfo pci;
			if (hasPciInfo && ((delegate* unmanaged[Cdecl]<nint, NvmlPciInfo*, int>) getPci)(device, &pci) == Success
				&& pci.PciDeviceId == expected)
			{
				return device;
			}
		}

		// A single NVIDIA board is unambiguous even when the PCI query is unavailable.
		return count == 1 ? first : 0;
	}
}
