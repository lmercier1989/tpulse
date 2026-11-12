using System.Globalization;

namespace Pulse.Core.Formatting;

/// <summary>
/// Display formatting with three significant digits. Throughput uses decimal SI prefixes
/// (1 MB/s = 1,000,000 B/s, 1 Mbps = 1,000,000 bit/s); memory sizes use binary prefixes like Windows does
/// (1 GB = 1024^3 bytes). The decimal separator follows the given culture (the user's culture by default).
/// </summary>
public static class Units
{
	/// <summary>Shown for a reading that is not available.</summary>
	public const string Unavailable = "n/a";

	private const double Promote = 999.5;

	private static readonly string[] ByteRateUnits = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
	private static readonly string[] BitRateUnits = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
	private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

	public static string Percent(double value, IFormatProvider? provider = null)
		=> double.IsFinite(value)
			? Math.Round(Math.Clamp(value, 0, 999), MidpointRounding.AwayFromZero).ToString("0", provider ?? CultureInfo.CurrentCulture) + "%"
			: Unavailable;

	/// <summary>
	/// The share of a process, in % of the machine, with one decimal: "41.2%", "0.4%". A graph shows whole percentages
	/// (<see cref="Percent"/>); the processes that make them up need the decimal to tell them apart.
	/// </summary>
	public static string Share(double value, IFormatProvider? provider = null)
		=> double.IsFinite(value) ? Math.Max(0, value).ToString("0.0", provider ?? CultureInfo.CurrentCulture) + "%" : Unavailable;

	/// <summary>
	/// A length of time, the one way Pulse writes it: "0.5 s", "45 s", "1 min 5 s", "12 min", "1 h 5 min", "2 d 3 h".
	/// Below 10 seconds it keeps up to two decimals; above, it rounds to the second and drops a zero second or minute.
	/// </summary>
	public static string Duration(double seconds, IFormatProvider? provider = null)
	{
		provider ??= CultureInfo.CurrentCulture;
		if (!double.IsFinite(seconds))
		{
			return Unavailable;
		}

		seconds = Math.Max(0, seconds);
		if (seconds < 10)
		{
			return seconds.ToString("0.##", provider) + " s";
		}

		var whole = (long) Math.Round(seconds, MidpointRounding.AwayFromZero);
		var (days, hours, minutes, rest) = (whole / 86400, whole / 3600 % 24, whole / 60 % 60, whole % 60);
		return whole < 60 ? $"{whole} s"
			: whole < 600 ? (rest == 0 ? $"{whole / 60} min" : $"{whole / 60} min {rest} s")
			: whole < 3600 ? $"{whole / 60} min"
			: whole < 86400 ? (minutes == 0 ? $"{hours} h" : $"{hours} h {minutes} min")
			: hours == 0 ? $"{days} d" : $"{days} d {hours} h";
	}

	public static string BytesPerSecond(double value, IFormatProvider? provider = null) => Scaled(value, 1000, ByteRateUnits, provider);

	public static string BitsPerSecond(double value, IFormatProvider? provider = null) => Scaled(value, 1000, BitRateUnits, provider);

	public static string Bytes(double value, IFormatProvider? provider = null) => Scaled(value, 1024, SizeUnits, provider);

	/// <summary>"12.3 / 31.7 GB": both sizes in the unit that suits <paramref name="total"/>.</summary>
	public static string BytesOf(double used, double total, IFormatProvider? provider = null)
	{
		if (!double.IsFinite(used) || !double.IsFinite(total) || total <= 0)
		{
			return Unavailable;
		}

		provider ??= CultureInfo.CurrentCulture;
		var unit = 0;
		var divisor = 1.0;
		while (unit < SizeUnits.Length - 1 && total / divisor >= Promote)
		{
			divisor *= 1024;
			unit++;
		}

		var format = unit == 0 ? "0" : "0.0";
		return $"{(Math.Max(0, used) / divisor).ToString(format, provider)} / {(total / divisor).ToString(format, provider)} {SizeUnits[unit]}";
	}

	/// <summary>"512", "12.4k", "1.24M" tokens.</summary>
	public static string Tokens(double value, IFormatProvider? provider = null)
	{
		if (!double.IsFinite(value))
		{
			return Unavailable;
		}

		provider ??= CultureInfo.CurrentCulture;
		value = Math.Max(0, value);
		return value < Promote ? Math.Round(value).ToString("0", provider)
			: value < Promote * 1000 ? Significant(value / 1000, provider) + "k"
			: Significant(value / 1_000_000, provider) + "M";
	}

	/// <summary>Any counter value: "0.0042", "12.5", "512", "12.4k", "1.24M", "3.10G"; three significant digits.</summary>
	public static string Number(double value, IFormatProvider? provider = null)
	{
		if (!double.IsFinite(value))
		{
			return Unavailable;
		}

		provider ??= CultureInfo.CurrentCulture;
		var size = Math.Abs(value);
		return size < Promote ? value.ToString("G3", provider)
			: size < Promote * 1000 ? Significant(value / 1000, provider) + "k"
			: size < Promote * 1_000_000 ? Significant(value / 1_000_000, provider) + "M"
			: Significant(value / 1_000_000_000, provider) + "G";
	}

	/// <summary>"512/min", "12.4k/min".</summary>
	public static string TokensPerMinute(double value, IFormatProvider? provider = null)
		=> double.IsFinite(value) ? Tokens(value, provider) + "/min" : Unavailable;

	public static string Celsius(double value, IFormatProvider? provider = null)
		=> double.IsFinite(value) ? value.ToString("0", provider ?? CultureInfo.CurrentCulture) + " °C" : Unavailable;

	public static string Watts(double value, IFormatProvider? provider = null)
		=> double.IsFinite(value) ? Math.Max(0, value).ToString("0", provider ?? CultureInfo.CurrentCulture) + " W" : Unavailable;

	private static string Scaled(double value, double step, string[] units, IFormatProvider? provider)
	{
		if (!double.IsFinite(value))
		{
			return Unavailable;
		}

		provider ??= CultureInfo.CurrentCulture;
		value = Math.Max(0, value);
		var unit = 0;
		while (unit < units.Length - 1 && value >= Promote)
		{
			value /= step;
			unit++;
		}

		var format = unit == 0 ? "0"
			: value < 9.995 ? "0.00"
			: value < 99.95 ? "0.0"
			: "0";
		return value.ToString(format, provider) + " " + units[unit];
	}

	private static string Significant(double value, IFormatProvider provider)
		=> value.ToString(value < 9.995 ? "0.00" : value < 99.95 ? "0.0" : "0", provider);
}
