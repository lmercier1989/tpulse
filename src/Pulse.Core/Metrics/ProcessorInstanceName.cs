using System.Globalization;

namespace Pulse.Core.Metrics;

/// <summary>
/// Parses processor counter instance names into a sortable key: <c>"0,13"</c> (Processor Information:
/// group, number) or <c>"13"</c> (Processor). Totals such as <c>"_Total"</c> and <c>"0,_Total"</c> are rejected.
/// </summary>
public static class ProcessorInstanceName
{
	public static bool TryParse(ReadOnlySpan<char> name, out int key)
	{
		key = 0;
		if (name.IsEmpty || name.Contains('_'))
		{
			return false;
		}

		var comma = name.IndexOf(',');
		if (comma < 0)
		{
			return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out key);
		}

		if (!int.TryParse(name[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out var group)
			|| !int.TryParse(name[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
		{
			return false;
		}

		key = (group * 1024) + number;
		return true;
	}
}
