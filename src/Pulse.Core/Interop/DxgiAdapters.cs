using Pulse.Core.Metrics;

namespace Pulse.Core.Interop;

/// <summary>A hardware graphics adapter as reported by DXGI.</summary>
/// <param name="Name">The adapter description, for example "NVIDIA GeForce GTX 1660 SUPER".</param>
/// <param name="VendorId">PCI vendor id (0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel).</param>
/// <param name="DeviceId">PCI device id.</param>
/// <param name="LuidLow">Low part of the adapter LUID.</param>
/// <param name="LuidHigh">High part of the adapter LUID.</param>
/// <param name="DedicatedVideoMemory">Dedicated video memory in bytes.</param>
internal readonly record struct GpuAdapter(string Name, uint VendorId, uint DeviceId, uint LuidLow, int LuidHigh, ulong DedicatedVideoMemory)
{
	/// <summary>The LUID packed the way <see cref="GpuInstanceName"/> parses it from counter instance names.</summary>
	public ulong Luid => GpuInstanceName.ComposeLuid(LuidHigh, LuidLow);
}

/// <summary>
/// Enumerates hardware adapters through DXGI. The COM interfaces are called through their vtables
/// (IDXGIFactory1::EnumAdapters1 is slot 12, IDXGIAdapter1::GetDesc1 slot 10, IUnknown::Release slot 2),
/// which avoids a COM interop layer for three calls made once at start-up.
/// </summary>
internal static unsafe class DxgiAdapters
{
	private static readonly Guid FactoryInterfaceId = new("770aae78-f26f-4dba-a829-253c83d1b387");

	public static List<GpuAdapter> Enumerate()
	{
		var adapters = new List<GpuAdapter>();
		if (Native.CreateDXGIFactory1(FactoryInterfaceId, out var factory) < 0 || factory == 0)
		{
			return adapters;
		}

		try
		{
			var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>) (*(void***) factory)[12];
			for (uint index = 0; ; index++)
			{
				nint adapter;
				if (enumAdapters1(factory, index, &adapter) < 0)
				{
					break; // DXGI_ERROR_NOT_FOUND marks the end of the list.
				}

				try
				{
					var getDesc1 = (delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc1*, int>) (*(void***) adapter)[10];
					DxgiAdapterDesc1 desc;
					if (getDesc1(adapter, &desc) < 0 || (desc.Flags & DxgiAdapterDesc1.SoftwareFlag) != 0)
					{
						continue; // Software rasterizers such as the Basic Render Driver.
					}

					var luid = GpuInstanceName.ComposeLuid(desc.LuidHigh, desc.LuidLow);
					if (adapters.Exists(known => known.Luid == luid))
					{
						continue; // The same adapter can be listed more than once.
					}

					adapters.Add(new GpuAdapter(
						new string(desc.Description).Trim(),
						desc.VendorId,
						desc.DeviceId,
						desc.LuidLow,
						desc.LuidHigh,
						desc.DedicatedVideoMemory));
				}
				finally
				{
					Release(adapter);
				}
			}
		}
		finally
		{
			Release(factory);
		}

		return adapters;
	}

	private static void Release(nint unknown)
		=> _ = ((delegate* unmanaged[Stdcall]<nint, uint>) (*(void***) unknown)[2])(unknown);
}
