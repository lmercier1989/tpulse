namespace Pulse.Core.Metrics;

/// <summary>How the value of a counter reads, guessed from its English name.</summary>
public enum CounterUnit
{
	/// <summary>A plain number: connections, queue length, requests.</summary>
	Number,

	/// <summary>A rate in events per second ("Requests/sec").</summary>
	PerSecond,

	/// <summary>A share ("% Processor Time").</summary>
	Percent,

	/// <summary>A size ("Available Bytes", "Private Bytes").</summary>
	Bytes,

	/// <summary>Throughput ("Disk Read Bytes/sec").</summary>
	BytesPerSecond,
}

/// <summary>
/// A performance counter path, <c>[\\machine]\Object(instance)\Counter</c>, as Performance Monitor and typeperf write it,
/// in English names. The leading backslash may be left out. An instance of <c>*</c> reads every instance and adds them up.
/// </summary>
public sealed record CounterPath(string? Machine, string Object, string? Instance, string Counter)
{
	/// <summary>Whether the instance has a wildcard, so the counter has several values to add up.</summary>
	public bool IsWildcard => Instance?.Contains('*', StringComparison.Ordinal) == true;

	/// <summary>"Web Service(_Total)": the object and its instance.</summary>
	public string Source => Instance is null ? Object : $"{Object}({Instance})";

	public CounterUnit Unit
	{
		get
		{
			var name = Counter.Trim();
			if (name.StartsWith('%'))
			{
				return CounterUnit.Percent;
			}

			if (name.EndsWith("Bytes/sec", StringComparison.OrdinalIgnoreCase))
			{
				return CounterUnit.BytesPerSecond;
			}

			// "Available MBytes" and "KBytes" carry their unit in the name: they read as plain numbers.
			if (name.EndsWith(" Bytes", StringComparison.OrdinalIgnoreCase) || name.Equals("Bytes", StringComparison.OrdinalIgnoreCase))
			{
				return CounterUnit.Bytes;
			}

			return name.EndsWith("/sec", StringComparison.OrdinalIgnoreCase) ? CounterUnit.PerSecond : CounterUnit.Number;
		}
	}

	/// <summary>Parses <c>[\\machine]\Object[(instance)]\Counter</c>; false for anything else.</summary>
	public static bool TryParse(string? text, out CounterPath path)
	{
		path = null!;
		var rest = text?.Trim().Trim('"') ?? string.Empty;
		string? machine = null;
		if (rest.StartsWith(@"\\", StringComparison.Ordinal))
		{
			var end = rest.IndexOf('\\', 2);
			if (end <= 2)
			{
				return false;
			}

			machine = rest[2..end];
			rest = rest[end..];
		}

		rest = rest.TrimStart('\\');

		// The counter name is after the last backslash that is not inside the instance's parentheses.
		var close = rest.LastIndexOf(')');
		var split = rest.LastIndexOf('\\');
		if (split <= 0 || split < close || split == rest.Length - 1)
		{
			return false;
		}

		var objectPart = rest[..split];
		var counter = rest[(split + 1)..];
		string? instance = null;
		var open = objectPart.IndexOf('(');
		if (open >= 0)
		{
			if (!objectPart.EndsWith(')') || open == 0)
			{
				return false;
			}

			instance = objectPart[(open + 1)..^1];
			objectPart = objectPart[..open];
		}

		if (string.IsNullOrWhiteSpace(objectPart) || string.IsNullOrWhiteSpace(counter) || objectPart.Contains('\\', StringComparison.Ordinal))
		{
			return false;
		}

		path = new CounterPath(machine, objectPart, instance, counter);
		return true;
	}

	/// <summary>The full English path, on <paramref name="host"/> when the path names no machine itself.</summary>
	public string ToPath(string? host)
	{
		var machine = Machine ?? host;
		var prefix = string.IsNullOrEmpty(machine) ? string.Empty : $@"\\{machine}";
		return $@"{prefix}\{Source}\{Counter}";
	}
}
