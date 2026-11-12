namespace Pulse.Core.Interop;

/// <summary>
/// A kernel-mode thunk handle to one adapter, used for the vendor-neutral adapter performance data
/// (temperature, fan speed) that WDDM 2.4+ drivers report. This is the source Task Manager uses for its
/// GPU temperature; it needs no administrator rights and no vendor SDK.
/// </summary>
internal sealed unsafe class D3dkmtAdapter : IDisposable
{
	private uint _handle;

	private D3dkmtAdapter(uint handle) => _handle = handle;

	public static D3dkmtAdapter? TryOpen(uint luidLow, int luidHigh)
	{
		var open = new D3dkmtOpenAdapterFromLuid { LuidLow = luidLow, LuidHigh = luidHigh };
		return Native.D3DKMTOpenAdapterFromLuid(ref open) == 0 ? new D3dkmtAdapter(open.Adapter) : null;
	}

	/// <summary>Queries the adapter performance data of physical adapter 0.</summary>
	public bool TryQuery(out D3dkmtAdapterPerfData data)
	{
		D3dkmtAdapterPerfData result = default;
		var query = new D3dkmtQueryAdapterInfo
		{
			Adapter = _handle,
			Type = D3dkmtQueryAdapterInfo.AdapterPerfDataType,
			PrivateDriverData = &result,
			PrivateDriverDataSize = (uint) sizeof(D3dkmtAdapterPerfData),
		};

		var ok = _handle != 0 && Native.D3DKMTQueryAdapterInfo(ref query) == 0;
		data = result;
		return ok;
	}

	public void Dispose()
	{
		if (_handle == 0)
		{
			return;
		}

		var close = new D3dkmtCloseAdapter { Adapter = _handle };
		_ = Native.D3DKMTCloseAdapter(ref close);
		_handle = 0;
	}
}
