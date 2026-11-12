using System.Globalization;

namespace Pulse.Core.CommandLine;

/// <summary>Parses durations such as "30", "30s", "1.5s", "500ms", "1m" or "5min" into seconds.</summary>
public static class DurationParser
{
	public static bool TryParseSeconds(string? text, out double seconds)
	{
		seconds = 0;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var span = text.Trim().ToLowerInvariant().AsSpan();
		var factor = 1.0;
		if (span.EndsWith("ms"))
		{
			factor = 0.001;
			span = span[..^2];
		}
		else if (span.EndsWith("min"))
		{
			factor = 60;
			span = span[..^3];
		}
		else if (span.EndsWith("sec"))
		{
			span = span[..^3];
		}
		else if (span.EndsWith('m'))
		{
			factor = 60;
			span = span[..^1];
		}
		else if (span.EndsWith('s'))
		{
			span = span[..^1];
		}

		if (!double.TryParse(span.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
			|| !double.IsFinite(value)
			|| value <= 0)
		{
			return false;
		}

		seconds = value * factor;
		return true;
	}
}
