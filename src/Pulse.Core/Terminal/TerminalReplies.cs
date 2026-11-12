using System.Globalization;
using System.Text.RegularExpressions;

namespace Pulse.Core.Terminal;

/// <summary>
/// What a terminal answered to the start-up queries: primary device attributes (DA1, <c>CSI c</c>), whose
/// attribute 4 means Sixel graphics, the cell size in pixels (<c>CSI 16 t</c>) and the text area size in
/// pixels (<c>CSI 14 t</c>).
/// </summary>
public sealed partial record TerminalReplies(bool? SupportsSixel, int? CellWidth, int? CellHeight, int? AreaWidth, int? AreaHeight)
{
	/// <summary>The queries, in the order the replies are expected; DA1 last, because every terminal answers it.</summary>
	public const string Query = "\u001b[16t\u001b[14t\u001b[c";

	/// <summary>Whether a DA1 reply (the last one expected) is in <paramref name="text"/>.</summary>
	public static bool IsComplete(string text) => DeviceAttributes().IsMatch(text);

	public static TerminalReplies Parse(string text)
	{
		bool? sixel = null;
		var attributes = DeviceAttributes().Match(text);
		if (attributes.Success)
		{
			// The first parameter is the conformance level; the attributes follow it.
			sixel = attributes.Groups[1].Value.Split(';').Skip(1).Contains("4");
		}

		var cell = CellSize().Match(text);
		var area = AreaSize().Match(text);
		return new TerminalReplies(
			sixel,
			cell.Success ? Number(cell.Groups[2].Value) : null,
			cell.Success ? Number(cell.Groups[1].Value) : null,
			area.Success ? Number(area.Groups[2].Value) : null,
			area.Success ? Number(area.Groups[1].Value) : null);
	}

	/// <summary>The cell size in pixels, from the cell size reply or else from the text area divided by the grid.</summary>
	public (int Width, int Height)? ResolveCellSize(int columns, int rows)
	{
		if (CellWidth > 0 && CellHeight > 0)
		{
			return (CellWidth.Value, CellHeight.Value);
		}

		if (AreaWidth > 0 && AreaHeight > 0 && columns > 0 && rows > 0)
		{
			return (AreaWidth.Value / columns, AreaHeight.Value / rows);
		}

		return null;
	}

	private static int? Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

	[GeneratedRegex(@"\u001b\[\?([0-9;]*)c")]
	private static partial Regex DeviceAttributes();

	[GeneratedRegex(@"\u001b\[6;(\d+);(\d+)t")]
	private static partial Regex CellSize();

	[GeneratedRegex(@"\u001b\[4;(\d+);(\d+)t")]
	private static partial Regex AreaSize();
}
