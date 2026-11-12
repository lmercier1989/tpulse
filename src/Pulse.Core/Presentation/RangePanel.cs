using System.Globalization;
using System.Text;

using Pulse.Core.Formatting;
using Pulse.Core.Metrics;
using Pulse.Core.Processing;

namespace Pulse.Core.Presentation;

/// <summary>One graph over a range of time: its title, the statistics of its main trace and how to show a value.</summary>
/// <param name="Title">"CPU", "Disk", the name of an extra graph.</param>
/// <param name="Stats">The statistics of the main trace over the range.</param>
/// <param name="Format">A value of the graph as text: "63%", "120 MB/s".</param>
/// <param name="Percent">Whether the values are percentages, so a difference is in points rather than a ratio.</param>
public sealed record RangeGraph(string Title, RangeStats Stats, Func<double, string> Format, bool Percent);

/// <summary>A range of time looked into: the graphs over it, the processes that carried the load and what happened.</summary>
/// <param name="From">The wall-clock start.</param>
/// <param name="To">The wall-clock end.</param>
/// <param name="Graphs">The shown graphs over the range.</param>
/// <param name="Cpu">The process names with the highest average CPU over the range.</param>
/// <param name="Io">The process names with the highest average I/O over the range.</param>
/// <param name="Events">The events in the range, oldest first.</param>
public sealed record RangeSummary(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<RangeGraph> Graphs, IReadOnlyList<ProcessLoad> Cpu, IReadOnlyList<ProcessLoad> Io, IReadOnlyList<PulseEvent> Events)
{
	public double Seconds => Math.Max(0, (To - From).TotalSeconds);
}

/// <summary>
/// What the side panel shows for a selected range of time, for a baseline to compare against and for the stacked CPU
/// graph, and the text that <c>Y</c> copies.
/// </summary>
public static class RangePanel
{
	/// <summary>The busiest processes listed for a range.</summary>
	public const int MaximumCpu = 5;

	public const int MaximumIo = 3;

	/// <summary>
	/// "SELECTION 14:32:05 · 1 min 5 s": a row per graph with its average and its peak under a header row, the busiest
	/// processes and the events.
	/// </summary>
	public static List<PanelLine> Selection(RangeSummary range)
	{
		var culture = CultureInfo.CurrentCulture;
		var lines = new List<PanelLine>
		{
			InfoPanel.Section("Selection", $"{range.From.LocalDateTime.ToString("T", culture)} · {Units.Duration(range.Seconds, culture)}"),
			Columns("average", "peak"),
		};
		foreach (var graph in range.Graphs.Where(graph => !graph.Stats.IsEmpty))
		{
			lines.Add(new PanelLine(PanelLineKind.Field, graph.Title, graph.Format(graph.Stats.Average), 1, Emphasis: true, Right: graph.Format(graph.Stats.Max)));
		}

		foreach (var load in range.Cpu.Take(MaximumCpu))
		{
			lines.Add(new PanelLine(PanelLineKind.Field, load.Name, InfoPanel.CountOf(load), 2, Right: InfoPanel.Cpu(load)));
		}

		foreach (var load in range.Io.Take(MaximumIo))
		{
			lines.Add(new PanelLine(PanelLineKind.Field, load.Name, InfoPanel.CountOf(load), 3, Right: InfoPanel.Io(load)));
		}

		foreach (var item in range.Events.TakeLast(InfoPanel.MaximumEvents))
		{
			lines.Add(new PanelLine(PanelLineKind.Field, item.Action, $"{item.Source} · {item.Text}", 3, Warning: item.Kind == EventKind.Error, Right: item.At.LocalDateTime.ToString("T", culture), Event: item.Kind));
		}

		lines.Add(new PanelLine(PanelLineKind.Blank, string.Empty, string.Empty, 9));
		return lines;
	}

	/// <summary>"VS BASELINE 14:20:00 · 1 min 10 s": a row per graph with the average now, the average then and the difference.</summary>
	public static List<PanelLine> Baseline(RangeSummary baseline, RangeSummary now)
	{
		var culture = CultureInfo.CurrentCulture;
		var lines = new List<PanelLine>
		{
			InfoPanel.Section("Vs baseline", $"{baseline.From.LocalDateTime.ToString("T", culture)} · {Units.Duration(baseline.Seconds, culture)}"),
			Columns("now vs then", "change"),
		};
		foreach (var (graph, before, difference, worse) in Compare(baseline, now))
		{
			lines.Add(new PanelLine(PanelLineKind.Field, graph.Title, $"{graph.Format(graph.Stats.Average)} vs {graph.Format(before.Stats.Average)}", 1, Warning: worse, Right: difference));
		}

		lines.Add(new PanelLine(PanelLineKind.Blank, string.Empty, string.Empty, 9));
		return lines;
	}

	/// <summary>
	/// "CPU BY PROCESS average": a row per band of the stacked CPU graph with its colour and its average, busiest first,
	/// and the rest up to the average total.
	/// </summary>
	public static List<PanelLine> Stack(IReadOnlyList<ProcessLoad> names, double averageTotal)
	{
		var lines = new List<PanelLine> { InfoPanel.Section("CPU by process", "average") };
		for (var i = 0; i < names.Count; i++)
		{
			lines.Add(new PanelLine(PanelLineKind.Field, names[i].Name, InfoPanel.CountOf(names[i]), 2, Right: InfoPanel.Cpu(names[i]), Swatch: i));
		}

		var other = double.IsFinite(averageTotal) ? Math.Max(0, averageTotal - names.Sum(name => name.Value)) : double.NaN;
		lines.Add(new PanelLine(PanelLineKind.Field, "other", string.Empty, 3, Right: double.IsFinite(other) ? Units.Share(other) : string.Empty, Swatch: CpuStack.Named));
		lines.Add(new PanelLine(PanelLineKind.Blank, string.Empty, string.Empty, 9));
		return lines;
	}

	/// <summary>The graphs both ranges have, with the difference of their averages: points for percentages, else a ratio.</summary>
	internal static IEnumerable<(RangeGraph Now, RangeGraph Before, string Difference, bool Worse)> Compare(RangeSummary baseline, RangeSummary now)
	{
		var culture = CultureInfo.CurrentCulture;
		foreach (var graph in now.Graphs.Where(graph => !graph.Stats.IsEmpty))
		{
			if (baseline.Graphs.FirstOrDefault(item => item.Title == graph.Title && !item.Stats.IsEmpty) is not { } before)
			{
				continue;
			}

			var a = graph.Stats.Average;
			var b = before.Stats.Average;
			if (graph.Percent)
			{
				var points = a - b;
				yield return (graph, before, $"{Signed(points, culture)} pt", points >= 10);
			}
			else if (b > 0)
			{
				var ratio = ((a / b) - 1) * 100;
				yield return (graph, before, $"{Signed(ratio, culture)}%", ratio >= 20);
			}
			else
			{
				yield return (graph, before, a > 0 ? "new" : "same", false);
			}
		}
	}

	/// <summary>The text <c>Y</c> copies: the range, a table of every graph, the busiest processes, the events and the baseline.</summary>
	public static string Report(RangeSummary range, RangeSummary? baseline, string? host)
	{
		var culture = CultureInfo.CurrentCulture;
		var text = new StringBuilder();
		text.Append("Pulse").Append(host is null ? string.Empty : $" · {host}").Append(" · ")
			.Append(Span(range, culture)).AppendLine().AppendLine();
		text.AppendLine($"{string.Empty,-12}{"lowest",-12}{"average",-12}{"p95",-12}peak");
		foreach (var graph in range.Graphs.Where(graph => !graph.Stats.IsEmpty))
		{
			var stats = graph.Stats;
			text.AppendLine($"{graph.Title,-12}{graph.Format(stats.Min),-12}{graph.Format(stats.Average),-12}{graph.Format(stats.P95),-12}{graph.Format(stats.Max)}");
		}

		if (range.Cpu.Count > 0)
		{
			text.AppendLine().Append("Busiest CPU   ").AppendLine(string.Join(", ", range.Cpu.Select(load => $"{Name(load)} {InfoPanel.Cpu(load)}")));
		}

		if (range.Io.Count > 0)
		{
			text.Append("Busiest I/O   ").AppendLine(string.Join(", ", range.Io.Select(load => $"{Name(load)} {InfoPanel.Io(load)}")));
		}

		foreach (var item in range.Events)
		{
			text.AppendLine($"{item.At.LocalDateTime.ToString("T", culture)}  {item.Action} · {item.Source} · {item.Text}");
		}

		if (baseline is not null)
		{
			text.AppendLine().Append("Against the baseline ").AppendLine(Span(baseline, culture));
			foreach (var (graph, before, difference, _) in Compare(baseline, range))
			{
				text.AppendLine($"{graph.Title,-12}average {graph.Format(graph.Stats.Average)} vs {graph.Format(before.Stats.Average)}   {difference}");
			}
		}

		return text.ToString().TrimEnd() + Environment.NewLine;
	}

	/// <summary>"14:32:05 to 14:33:10 (1 min 5 s)".</summary>
	private static string Span(RangeSummary range, CultureInfo culture)
		=> $"{range.From.LocalDateTime.ToString("T", culture)} to {range.To.LocalDateTime.ToString("T", culture)} ({Units.Duration(range.Seconds, culture)})";

	/// <summary>A dim row naming the columns of the rows under it: the value column and the right-aligned one.</summary>
	private static PanelLine Columns(string text, string right) => new(PanelLineKind.Note, string.Empty, text, 1, Right: right);

	private static string Name(ProcessLoad load) => load.Count > 1 ? $"{load.Name} ×{load.Count}" : load.Name;

	private static string Signed(double value, CultureInfo culture)
		=> (value >= 0 ? "+" : "-") + Math.Abs(value).ToString("0", culture);
}
