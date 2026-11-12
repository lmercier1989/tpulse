namespace Pulse.Core.Presentation;

/// <summary>
/// Panel lines as plain text: the rows the terminal draws in its side panel and flyout, laid out the same way (label
/// column, value, right-aligned figure) for the tooltip of the window, so both show a moment alike.
/// </summary>
public static class PanelText
{
	/// <summary>Every line of <paramref name="lines"/> in <paramref name="width"/> columns.</summary>
	public static List<string> Render(IReadOnlyList<PanelLine> lines, int width)
	{
		var labelWidths = InfoPanel.LabelWidths(lines, width);
		var output = new List<string>(lines.Count);
		for (var i = 0; i < lines.Count; i++)
		{
			var line = lines[i];
			var labelWidth = Math.Min(labelWidths[i], width);
			output.Add(line.Kind switch
			{
				PanelLineKind.Section => Fit(line.Text.Length > 0 ? $"{line.Label}  {line.Text}" : line.Label, width),
				PanelLineKind.Field => Fit(line.Label, labelWidth - 1).PadRight(labelWidth) + Value(line.Text, line.Right, width - labelWidth),
				PanelLineKind.Note => new string(' ', labelWidth) + Value(line.Text, line.Right, width - labelWidth),
				PanelLineKind.Gauge => Fit($"{line.Label} {(double.IsFinite(line.Percent) ? line.Percent.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%" : string.Empty)}", width),
				_ => string.Empty,
			});
		}

		return output;
	}

	/// <summary>The text left and the figure right-aligned in <paramref name="room"/> columns; the text gives way first.</summary>
	private static string Value(string text, string right, int room)
	{
		if (room <= 0)
		{
			return string.Empty;
		}

		right = Fit(right, room);
		var textRoom = right.Length == 0 ? room : room - right.Length - 2;
		var left = textRoom > 0 ? Fit(text, textRoom) : string.Empty;
		return right.Length == 0 ? left : left.PadRight(room - right.Length) + right;
	}

	private static string Fit(string text, int width)
		=> width <= 0 ? string.Empty : text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";
}
