using System.Globalization;

namespace Pulse.Core.Metrics;

/// <summary>
/// Parses the instance names of the GPU performance counters without allocating, for example
/// <c>pid_4568_luid_0x00000000_0x0000F1B4_phys_0_eng_3_engtype_VideoDecode</c> (GPU Engine) and
/// <c>luid_0x00000000_0x0000F1B4_phys_0</c> (GPU Adapter Memory).
/// </summary>
public static class GpuInstanceName
{
	/// <summary>Parses a GPU Engine instance into its adapter LUID, physical adapter and engine index.</summary>
	public static bool TryParseEngine(ReadOnlySpan<char> name, out ulong luid, out int physical, out int engine)
	{
		physical = 0;
		engine = 0;
		return TryParseLuid(name, out luid, out var rest)
			   && TryParseNumberAfter(rest, "_phys_", out physical)
			   && TryParseNumberAfter(rest, "_eng_", out engine);
	}

	/// <summary>Parses a GPU Adapter Memory instance into its adapter LUID and physical adapter.</summary>
	public static bool TryParseAdapter(ReadOnlySpan<char> name, out ulong luid, out int physical)
	{
		physical = 0;
		return TryParseLuid(name, out luid, out var rest) && TryParseNumberAfter(rest, "_phys_", out physical);
	}

	/// <summary>Packs a LUID the same way the parser does: high part in the upper 32 bits.</summary>
	public static ulong ComposeLuid(int high, uint low) => ((ulong) (uint) high << 32) | low;

	private static bool TryParseLuid(ReadOnlySpan<char> name, out ulong luid, out ReadOnlySpan<char> rest)
	{
		luid = 0;
		rest = default;

		var start = name.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
		if (start < 0)
		{
			return false;
		}

		var span = name[(start + 7)..];
		var separator = span.IndexOf("_0x", StringComparison.OrdinalIgnoreCase);
		if (separator <= 0 || !uint.TryParse(span[..separator], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high))
		{
			return false;
		}

		span = span[(separator + 3)..];
		var end = span.IndexOf('_');
		var lowText = end < 0 ? span : span[..end];
		if (!uint.TryParse(lowText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low))
		{
			return false;
		}

		luid = ((ulong) high << 32) | low;
		rest = end < 0 ? ReadOnlySpan<char>.Empty : span[end..];
		return true;
	}

	private static bool TryParseNumberAfter(ReadOnlySpan<char> text, string marker, out int value)
	{
		value = 0;
		var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (start < 0)
		{
			return false;
		}

		var digits = text[(start + marker.Length)..];
		var length = 0;
		while (length < digits.Length && char.IsAsciiDigit(digits[length]))
		{
			length++;
		}

		return length > 0 && int.TryParse(digits[..length], NumberStyles.None, CultureInfo.InvariantCulture, out value);
	}
}
