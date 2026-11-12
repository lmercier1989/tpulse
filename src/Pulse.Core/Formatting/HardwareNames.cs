using System.Text.RegularExpressions;

namespace Pulse.Core.Formatting;

/// <summary>Shortens marketing processor names for a compact label.</summary>
public static partial class HardwareNames
{
	private static readonly string[] TrademarkTokens = ["(R)", "(r)", "(TM)", "(tm)", "®", "™"];

	/// <summary>
	/// "Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz" becomes "Intel Core i7-8700";
	/// "AMD Ryzen 7 5800X 8-Core Processor" becomes "AMD Ryzen 7 5800X 8-Core".
	/// </summary>
	public static string CleanCpuName(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "CPU";
		}

		var name = raw;
		foreach (var token in TrademarkTokens)
		{
			name = name.Replace(token, " ", StringComparison.Ordinal);
		}

		var at = name.IndexOf('@');
		if (at >= 0)
		{
			name = name[..at];
		}

		name = NoiseWords().Replace(name, " ");
		name = Whitespace().Replace(name, " ").Trim();
		return name.Length == 0 ? "CPU" : name;
	}

	[GeneratedRegex(@"\b(CPU|Processor)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex NoiseWords();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
