namespace Pulse.Core.Metrics;

/// <summary>The text of an error from the system logs, as the panel and the markers show it.</summary>
internal static class EventText
{
	/// <summary>The longest text kept of an event.</summary>
	public const int TextLength = 160;

	/// <summary>The first line of an event description, cut to a panel row: "Faulting application name: devenv.exe, ..."</summary>
	public static string FirstLine(string? description)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			return string.Empty;
		}

		var text = description.AsSpan().Trim();
		var end = text.IndexOfAny('\r', '\n');
		if (end >= 0)
		{
			text = text[..end];
		}

		return text.Length > TextLength ? string.Concat(text[..(TextLength - 1)], "…") : text.ToString();
	}
}
