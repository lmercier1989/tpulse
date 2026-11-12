using System.Runtime.InteropServices;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Interop;

/// <summary>
/// One PDH query that collects every counter Pulse uses in a single <c>PdhCollectQueryData</c> call.
/// Counters are added by their English path so the query works on localized Windows installations.
/// Wildcard counters are read into one reusable native buffer, so a read does not allocate.
/// </summary>
internal sealed unsafe class PdhQuery : IDisposable
{
	private const uint Format = Native.PdhFmtDouble | Native.PdhFmtNoCap100;

	private nint _query;
	private void* _buffer;
	private uint _capacity;

	private PdhQuery(nint query) => _query = query;

	/// <summary>Opens a real-time query, or returns null (and logs) when PDH is not usable.</summary>
	public static PdhQuery? TryOpen()
	{
		var status = Native.PdhOpenQuery(null, 0, out var query);
		if (status == Native.ErrorSuccess)
		{
			return new PdhQuery(query);
		}

		Log.Warn($"PdhOpenQuery failed (0x{status:X8}); performance counters are unavailable.");
		return null;
	}

	/// <summary>Adds a counter by its English path; returns the PDH status, 0 on success.</summary>
	public uint AddCounter(string englishPath, out nint counter) => Native.PdhAddEnglishCounter(_query, englishPath, 0, out counter);

	/// <summary>Adds a counter by its English path; returns 0 when the counter does not exist here.</summary>
	public nint TryAddCounter(string englishPath)
	{
		var status = AddCounter(englishPath, out var counter);
		if (status == Native.ErrorSuccess)
		{
			Log.Info($"Counter available: {englishPath}");
			return counter;
		}

		Log.Info($"Counter unavailable: {englishPath} (0x{status:X8})");
		return 0;
	}

	/// <summary>Samples every counter of the query.</summary>
	public void Collect()
	{
		var status = Native.PdhCollectQueryData(_query);
		if (status != Native.ErrorSuccess)
		{
			Log.WarnOnce($"pdh-collect-{status:X8}", $"PdhCollectQueryData returned 0x{status:X8}; some counters may be missing this sample.");
		}
	}

	/// <summary>Reads a single-instance counter, or NaN when it has no valid value (yet).</summary>
	public double ReadValue(nint counter)
	{
		if (counter == 0)
		{
			return double.NaN;
		}

		var status = Native.PdhGetFormattedCounterValue(counter, Format, out _, out var value);
		return status == Native.ErrorSuccess && IsValid(value.CStatus) ? value.DoubleValue : double.NaN;
	}

	/// <summary>
	/// Reads every instance of a wildcard counter. The returned items and their names point into a buffer
	/// owned by this query and stay valid only until the next <see cref="ReadArray"/> call.
	/// </summary>
	public ReadOnlySpan<PdhFmtCounterValueItem> ReadArray(nint counter)
	{
		if (counter == 0)
		{
			return default;
		}

		// Instances can appear between the size probe and the read, so retry a few times.
		for (var attempt = 0; attempt < 4; attempt++)
		{
			var size = _capacity;
			var status = Native.PdhGetFormattedCounterArray(counter, Format, ref size, out var count, _buffer);
			if (status == Native.ErrorSuccess)
			{
				return new ReadOnlySpan<PdhFmtCounterValueItem>(_buffer, (int) count);
			}

			if (status != Native.PdhMoreData)
			{
				// PDH_INVALID_DATA on the first sample of a rate counter, or no instances at all.
				return default;
			}

			Grow(size);
		}

		return default;
	}

	/// <summary>Whether a counter status carries usable data.</summary>
	public static bool IsValid(uint status) => status is Native.PdhCStatusValidData or Native.PdhCStatusNewData;

	/// <summary>The instance name of an item read by <see cref="ReadArray"/>, without allocating.</summary>
	public static ReadOnlySpan<char> NameOf(in PdhFmtCounterValueItem item)
		=> item.Name == 0 ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*) item.Name);

	public void Dispose()
	{
		if (_query != 0)
		{
			_ = Native.PdhCloseQuery(_query);
			_query = 0;
		}

		if (_buffer is not null)
		{
			NativeMemory.Free(_buffer);
			_buffer = null;
			_capacity = 0;
		}
	}

	private void Grow(uint required)
	{
		var capacity = Math.Max(required + required / 4, Math.Max(_capacity * 2, 4096u));
		NativeMemory.Free(_buffer);
		_buffer = NativeMemory.Alloc(capacity);
		_capacity = capacity;
	}
}
