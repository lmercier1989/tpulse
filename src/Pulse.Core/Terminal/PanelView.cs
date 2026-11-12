using System.Globalization;

using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Core.Terminal;

/// <summary>
/// Writes rows of the info panel. Each row first erases its own cells (and only those), so the graphs beside
/// it are never touched. A section title runs into a thin rule across the column, so sections stay apart without blank
/// rows. Labels share one column; values start after it, and short values (loads, sizes, times) are right-aligned, so
/// they line up for comparing. Gauges are a thin line, heavy where the volume is used, turning red from 90 % full.
/// </summary>
public static class PanelView
{
	private const string Used = "━";
	private const string Free = "─";
	private const string Rule = "─";
	private const int GaugeLabelWidth = 4;
	private const double FullPercent = 90;

	public static readonly Rgb Warning = ScopeColors.Warning;

	/// <summary>
	/// Writes <paramref name="line"/> at <paramref name="row"/>, with values after a label column of <paramref name="labelWidth"/>,
	/// on <paramref name="background"/> (the panel colour when null). The row is erased first unless <paramref name="erase"/> is
	/// false: a line over the graphs (the flyout) is blanked by its caller instead, because erasing a cell of a Sixel
	/// image deletes the whole image in Konsole.
	/// </summary>
	public static void WriteLine(AnsiWriter output, int row, int left, int width, PanelLine line, int labelWidth = InfoPanel.LabelWidth, Rgb? background = null, bool erase = true)
	{
		output.MoveTo(row, left).Background(background ?? ScopeColors.Background);
		if (erase)
		{
			output.Ascii("\u001b[").Number(width).Byte('X');
		}

		if (width <= 0)
		{
			return;
		}

		switch (line.Kind)
		{
			case PanelLineKind.Section:
				var title = HeaderLine.Fit(line.Label, width);
				output.Bold().Foreground(ScopeColors.TextPrimary).Text(title).NotBold();
				var used = title.Length;
				if (line.Text.Length > 0 && width - used > 4)
				{
					var detail = HeaderLine.Fit(line.Text, width - used - 1);
					output.Text(" ").Foreground(ScopeColors.TextSecondary).Text(detail);
					used += detail.Length + 1;
				}

				if (width - used > 2)
				{
					output.Text(" ").Foreground(ScopeColors.Over(ScopeColors.White, 0.14)).Text(Repeat(Rule, width - used - 1));
				}

				break;

			case PanelLineKind.Field:
				var swatch = labelWidth > 4 ? line.MarkWidth : 0;
				if (swatch > 0 && line.Event is { } kind)
				{
					output.Foreground(ScopeColors.MarkerOf(kind)).Text("│ ");
				}
				else if (swatch > 0)
				{
					output.Foreground(ScopeColors.Stack(line.Swatch)).Text("■ ");
				}

				var label = HeaderLine.Fit(line.Label, labelWidth - 1 - swatch).PadRight(labelWidth - swatch);
				output.Foreground(ScopeColors.TextDim).Text(HeaderLine.Fit(label, width - swatch));
				if (width > labelWidth)
				{
					var color = line.Warning ? Warning : line.Emphasis ? ScopeColors.TextPrimary : ScopeColors.TextSecondary;
					WriteValue(output, row, left + labelWidth, width - labelWidth, line.Text, line.Right, color, line.Warning);
				}

				break;

			case PanelLineKind.Note:
				var indent = Math.Min(labelWidth, width);
				output.MoveTo(row, left + indent);
				WriteValue(output, row, left + indent, width - indent, line.Text, line.Right, line.Warning ? Warning : ScopeColors.TextDim, line.Warning);
				break;

			case PanelLineKind.Gauge:
				WriteGauge(output, width, line);
				break;
		}
	}

	/// <summary>
	/// The text left in <paramref name="room"/> columns and the short value right-aligned at their end; the text gives way
	/// first, the value only when it does not fit on its own.
	/// </summary>
	private static void WriteValue(AnsiWriter output, int row, int column, int room, string text, string right, Rgb color, bool warning)
	{
		if (room <= 0)
		{
			return;
		}

		right = HeaderLine.Fit(right, room);
		var textRoom = right.Length == 0 ? room : room - right.Length - 2;
		if (textRoom > 0 && text.Length > 0)
		{
			output.Foreground(color).Text(HeaderLine.Fit(text, textRoom));
		}

		if (right.Length > 0)
		{
			output.MoveTo(row, column + room - right.Length).Foreground(warning ? Warning : ScopeColors.TextSecondary).Text(right);
		}
	}

	private static void WriteGauge(AnsiWriter output, int width, PanelLine line)
	{
		var percent = double.IsFinite(line.Percent) ? Math.Clamp(line.Percent, 0, 100) : 0;
		var value = double.IsFinite(line.Percent) ? percent.ToString("0", CultureInfo.CurrentCulture) + "%" : "n/a";
		var bar = width - GaugeLabelWidth - 5;
		output.Foreground(ScopeColors.TextSecondary).Text(HeaderLine.Fit(line.Label, GaugeLabelWidth - 1).PadRight(GaugeLabelWidth));
		if (bar >= 4)
		{
			var used = (int) Math.Round(bar * percent / 100);
			output.Foreground(percent >= FullPercent ? Warning : ScopeColors.For(MetricKind.Disk)).Text(Repeat(Used, used))
				.Foreground(ScopeColors.Over(ScopeColors.White, 0.16)).Text(Repeat(Free, bar - used))
				.Text(" ");
		}

		output.Foreground(percent >= FullPercent ? Warning : ScopeColors.TextPrimary).Text(value.PadLeft(4));
	}

	private static string Repeat(string text, int count) => count <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(text, count));
}
