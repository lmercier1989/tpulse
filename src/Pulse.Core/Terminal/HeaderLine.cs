using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Core.Terminal;

/// <summary>
/// Writes the text rows of the terminal screen: a graph header (dot, name, details on the left; window
/// statistics and current values on the right) and the footer. Text that does not fit is dropped from the
/// least to the most important part, and the details are cut with an ellipsis, so a row never wraps.
/// </summary>
public static class HeaderLine
{
	private const string Ellipsis = "…";
	private const string Key = "━━ ";

	public static void Write(AnsiWriter output, int row, int left, int width, MetricKind kind, ChannelReadout readout, bool compact)
	{
		var primary = ScopeColors.For(kind);
		var secondary = ScopeColors.SecondaryOf(kind);
		var title = readout.Title.ToUpperInvariant();

		var values = BuildValues(readout, primary, secondary);
		var stats = compact ? string.Empty : Join(readout.Stats, readout.Scale.Length > 0 ? $"scale {readout.Scale}" : string.Empty);
		var fixedLeft = 2 + title.Length;

		// Drop parts until the row fits: statistics first, then the second value.
		while (true)
		{
			var right = TextWidth(values) + (stats.Length > 0 ? stats.Length + 3 : 0);
			if (fixedLeft + 2 + right <= width || (stats.Length == 0 && values.Count <= 1))
			{
				break;
			}

			if (stats.Length > 0)
			{
				stats = string.Empty;
			}
			else
			{
				values.RemoveAt(values.Count - 1);
			}
		}

		var rightWidth = TextWidth(values) + (stats.Length > 0 ? stats.Length + 3 : 0);
		var detailRoom = width - fixedLeft - 2 - rightWidth - 2;
		var detail = compact || readout.Unavailable is not null ? readout.Unavailable ?? string.Empty : readout.Detail;
		// A detail cut to a few letters says nothing; leave it out instead.
		detail = detailRoom >= 10 ? Fit(detail, detailRoom) : string.Empty;

		// Erase only the graph columns of the row: the info panel may share it.
		output.MoveTo(row, 0).Background(ScopeColors.Background).Ascii("\u001b[").Number(left + width).Byte('X').MoveTo(row, left)
			.Foreground(primary).Text("● ")
			.Bold().Foreground(ScopeColors.TextPrimary).Text(title).NotBold();
		if (detail.Length > 0)
		{
			output.Text("  ").Foreground(ScopeColors.TextSecondary).Text(detail);
		}

		if (rightWidth > 0 && rightWidth <= width)
		{
			output.MoveTo(row, left + width - rightWidth);
			if (stats.Length > 0)
			{
				output.Foreground(ScopeColors.TextDim).Text(stats).Text("   ");
			}

			for (var i = 0; i < values.Count; i++)
			{
				var value = values[i];
				if (i > 0)
				{
					output.Text("  ");
				}

				if (value.Key is { } keyColor)
				{
					output.Foreground(keyColor).Text(Key);
				}

				if (value.Label.Length > 0)
				{
					output.Foreground(ScopeColors.TextSecondary).Text(value.Label).Text(" ");
				}

				output.Bold().Foreground(value.Alert ? ScopeColors.Warning : ScopeColors.TextPrimary).Text(value.Text).NotBold();
			}
		}
	}

	/// <summary>
	/// The footer: key hints on the left, state (history, interval, renderer, pause, messages) on the right. The part under
	/// the mouse that a click works (<paramref name="hover"/>, from <see cref="FooterLine"/>) is lit like a button.
	/// </summary>
	public static void WriteFooter(AnsiWriter output, int row, int left, int width, string hints, string status, bool highlightStatus, (int Start, int Length)? hover = null, Rgb? hoverBackground = null)
	{
		(hints, status) = FitFooter(width, hints, status);
		output.MoveTo(row, 0).Background(ScopeColors.Background).EraseLine().MoveTo(row, left)
			.Foreground(ScopeColors.TextDim).Text(hints)
			.MoveTo(row, left + width - status.Length)
			.Foreground(highlightStatus ? ScopeColors.TextPrimary : ScopeColors.TextSecondary).Text(status);
		if (hover is { } part && FooterLine(width, hints, status) is var line && part.Start >= 0 && part.Start + part.Length <= line.Length)
		{
			output.MoveTo(row, left + part.Start).Background(hoverBackground ?? ScopeColors.Background).Foreground(ScopeColors.TextPrimary)
				.Text(line.AsSpan(part.Start, part.Length)).Background(ScopeColors.Background);
		}
	}

	/// <summary>The footer as text, <paramref name="width"/> columns: what a click on it is matched against.</summary>
	public static string FooterLine(int width, string hints, string status)
	{
		(hints, status) = FitFooter(width, hints, status);
		return hints + new string(' ', Math.Max(0, width - hints.Length - status.Length)) + status;
	}

	/// <summary>The hints and the state cut to fit <paramref name="width"/> together; the state goes first.</summary>
	private static (string Hints, string Status) FitFooter(int width, string hints, string status)
	{
		status = Fit(status, width);
		return (Fit(hints, width - status.Length - 3), status);
	}

	/// <summary>Cuts <paramref name="text"/> to <paramref name="room"/> columns, ending in an ellipsis when cut.</summary>
	public static string Fit(string text, int room)
	{
		if (room <= 0)
		{
			return string.Empty;
		}

		if (text.Length <= room)
		{
			return text;
		}

		return room == 1 ? Ellipsis : string.Concat(text.AsSpan(0, room - 1).TrimEnd(), Ellipsis);
	}

	private static List<Reading> BuildValues(ChannelReadout readout, Rgb primary, Rgb secondary)
	{
		var values = new List<Reading>(2)
		{
			new(readout.HasSecondary ? primary : null, readout.PrimaryLabel, readout.PrimaryValue, readout.Alert),
		};
		if (readout.HasSecondary && readout.SecondaryValue.Length > 0)
		{
			values.Add(new Reading(secondary, readout.SecondaryLabel, readout.SecondaryValue, false));
		}

		return values;
	}

	private static int TextWidth(List<Reading> values)
	{
		var width = 0;
		for (var i = 0; i < values.Count; i++)
		{
			var value = values[i];
			width += (i > 0 ? 2 : 0) + (value.Key is null ? 0 : Key.Length) + (value.Label.Length > 0 ? value.Label.Length + 1 : 0) + value.Text.Length;
		}

		return width;
	}

	private static string Join(string first, string second)
		=> first.Length == 0 ? second : second.Length == 0 ? first : $"{first} · {second}";

	private readonly record struct Reading(Rgb? Key, string Label, string Text, bool Alert);
}
