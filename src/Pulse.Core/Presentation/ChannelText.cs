using System.Globalization;

using Pulse.Core.Formatting;
using Pulse.Core.Metrics;
using Pulse.Core.Processing;

namespace Pulse.Core.Presentation;

/// <summary>The text of one graph's header line, shared by the terminal and the window.</summary>
public sealed record ChannelReadout
{
	public required string Title { get; init; }

	public string Detail { get; init; } = string.Empty;

	/// <summary>Min / max (percentages) or peaks (throughput) of the visible window.</summary>
	public string Stats { get; init; } = string.Empty;

	/// <summary>The ceiling of an automatic scale ("200 MB/s"); empty for fixed scales.</summary>
	public string Scale { get; init; } = string.Empty;

	public string PrimaryLabel { get; init; } = string.Empty;

	public string PrimaryValue { get; init; } = string.Empty;

	public string SecondaryLabel { get; init; } = string.Empty;

	public string SecondaryValue { get; init; } = string.Empty;

	public bool HasSecondary { get; init; }

	/// <summary>Why the graph has no data source on this system; null when it has one.</summary>
	public string? Unavailable { get; init; }

	/// <summary>The load has stayed at or above 90 % for a while: the value is shown in the warning colour.</summary>
	public bool Alert { get; init; }
}

/// <summary>Turns a channel and the latest sample into header text.</summary>
public static class ChannelText
{
	private const string Separator = " · ";

	/// <summary>A load at or above this share, for <see cref="AlertSeconds"/>, turns the value red.</summary>
	public const double AlertPercent = 90;

	public const double AlertSeconds = 10;

	public static string TitleOf(MetricKind kind) => kind switch
	{
		MetricKind.Cpu => "CPU",
		MetricKind.Gpu => "GPU",
		MetricKind.Memory => "Memory",
		MetricKind.Disk => "Disk",
		MetricKind.Network => "Network",
		MetricKind.Process => "Process",
		MetricKind.Counter => "Counter",
		MetricKind.Git => "Git",
		_ => "Claude",
	};

	/// <summary>The title of a graph: the process or counter name for an extra graph.</summary>
	public static string TitleOf(Channel channel) => channel.Watch?.Title ?? TitleOf(channel.Kind);

	/// <summary>The labels in front of the primary and secondary values.</summary>
	public static (string Primary, string Secondary) ValueLabelsOf(MetricKind kind) => kind switch
	{
		MetricKind.Cpu => (string.Empty, "claude"),
		MetricKind.Gpu => (string.Empty, "VRAM"),
		MetricKind.Memory => (string.Empty, "claude"),
		MetricKind.Disk => ("read", "write"),
		MetricKind.Network => ("down", "up"),
		MetricKind.Process => ("cpu", "mem"),
		MetricKind.Counter or MetricKind.Git => (string.Empty, string.Empty),
		_ => ("out", "in"),
	};

	public static string? UnavailableReasonOf(MetricKind kind, SystemInfo info) => kind switch
	{
		MetricKind.Gpu or MetricKind.Claude when info.Host is not null => $"{TitleOf(kind)} is read on this machine only, not on {info.Host}",
		MetricKind.Cpu when !info.HasCpu => "CPU performance counters are not available",
		MetricKind.Gpu when info.GpuName is null => "No hardware GPU found",
		MetricKind.Gpu when !info.HasGpu => "GPU performance counters are not available",
		MetricKind.Memory when !info.HasMemory => "Memory status is not available",
		MetricKind.Disk when !info.HasDisk => "Disk performance counters are not available",
		MetricKind.Network when !info.HasNetwork => "Network performance counters are not available",
		MetricKind.Claude when !info.HasClaude => "Claude was not found on this machine",
		MetricKind.Git when !info.FollowsGit => info.Host is not null
			? "Git follows the repositories of this machine; add -git to see them next to " + info.Host
			: "Git is not followed in this run (start tpulse with -git)",
		_ => null,
	};

	/// <summary>The header for <paramref name="channel"/>; before the first sample only the static parts are filled.</summary>
	public static ChannelReadout Describe(Channel channel, SystemInfo info, MetricSnapshot? sample, bool showStats)
	{
		var kind = channel.Kind;
		var (primaryLabel, secondaryLabel) = ValueLabelsOf(kind);
		var readout = new ChannelReadout
		{
			Title = TitleOf(channel),
			PrimaryLabel = primaryLabel,
			SecondaryLabel = secondaryLabel,
			// Claude's share in the CPU and memory graphs only where Claude can be followed.
			HasSecondary = channel.Secondary is not null && (info.HasClaude || kind is not (MetricKind.Cpu or MetricKind.Memory)),
			Unavailable = UnavailableReasonOf(kind, info),
		};

		if (readout.Unavailable is not null)
		{
			return readout with { PrimaryLabel = string.Empty, PrimaryValue = Units.Unavailable, HasSecondary = false };
		}

		if (sample is null)
		{
			return readout with { Detail = InitialDetail(channel, info) };
		}

		var scale = channel.Scale.IsFixed ? string.Empty : Format(channel, channel.Scale.Target);
		var alert = kind is MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory
					&& channel.IsSustainedAbove(AlertPercent, AlertSeconds, sample.Time);
		readout = readout with { Alert = alert };
		return kind switch
		{
			MetricKind.Cpu => readout with
			{
				PrimaryValue = Units.Percent(sample.CpuPercent),
				SecondaryValue = Units.Percent(sample.ClaudeCpuPercent),
				// The only temperature Windows exposes without a kernel driver is the ACPI thermal zone,
				// which is often a board sensor rather than the CPU package, so it is labelled as such.
				Detail = Join(
					info.CpuName,
					Threads(info, sample),
					double.IsFinite(sample.CpuTemperatureCelsius) ? $"ACPI zone {Units.Celsius(sample.CpuTemperatureCelsius)}" : null),
				Stats = showStats ? MinMax(channel.PrimaryRange, Units.Percent) : string.Empty,
			},
			MetricKind.Gpu => readout with
			{
				PrimaryValue = Units.Percent(sample.GpuPercent),
				SecondaryValue = Units.Percent(sample.GpuMemoryPercent),
				Detail = Join(
					info.GpuName,
					double.IsFinite(sample.GpuMemoryUsedBytes) ? Units.BytesOf(sample.GpuMemoryUsedBytes, info.GpuDedicatedBytes) : null,
					Optional(sample.GpuTemperatureCelsius, Units.Celsius),
					Optional(sample.GpuPowerWatts, Units.Watts)),
				Stats = showStats ? MinMax(channel.PrimaryRange, Units.Percent) : string.Empty,
			},
			MetricKind.Memory => readout with
			{
				PrimaryValue = Units.Percent(sample.MemoryPercent),
				SecondaryValue = Units.Percent(sample.ClaudeMemoryPercent),
				Detail = Join(
					$"{Units.BytesOf(sample.MemoryUsedBytes, sample.MemoryTotalBytes)} used",
					$"{Units.Bytes(sample.MemoryAvailableBytes)} available"),
				Stats = showStats ? MinMax(channel.PrimaryRange, Units.Percent) : string.Empty,
			},
			MetricKind.Disk => readout with
			{
				PrimaryValue = Units.BytesPerSecond(sample.DiskReadBytesPerSecond),
				SecondaryValue = Units.BytesPerSecond(sample.DiskWriteBytesPerSecond),
				Detail = double.IsFinite(sample.DiskActivePercent) ? $"busiest disk {Units.Percent(sample.DiskActivePercent)} active" : "all physical disks",
				Stats = showStats ? Peaks(channel, Units.BytesPerSecond) : string.Empty,
				Scale = scale,
			},
			MetricKind.Network => readout with
			{
				PrimaryValue = Units.BitsPerSecond(sample.NetworkDownBitsPerSecond),
				SecondaryValue = Units.BitsPerSecond(sample.NetworkUpBitsPerSecond),
				Detail = info.NetworkAdapters ?? "no physical adapter is up",
				Stats = showStats ? Peaks(channel, Units.BitsPerSecond) : string.Empty,
				Scale = scale,
			},
			MetricKind.Process => DescribeProcess(readout, channel, Reading(channel, sample), showStats, scale),
			MetricKind.Counter => DescribeCounter(readout, channel, Reading(channel, sample), info, showStats, scale),
			MetricKind.Git => DescribeGit(readout, channel, sample, showStats, scale),
			_ => readout with
			{
				PrimaryValue = Units.TokensPerMinute(sample.ClaudeOutputPerMinute),
				SecondaryValue = Units.TokensPerMinute(sample.ClaudeInputPerMinute),
				Detail = ClaudeDetail(sample),
				Stats = showStats ? Peaks(channel, Units.TokensPerMinute) : string.Empty,
				Scale = scale,
			},
		};
	}

	/// <summary>A value of the graph in its own unit: the scale label and the statistics of an extra graph use it too.</summary>
	public static string Format(Channel channel, double value, IFormatProvider? provider = null) => channel.Kind switch
	{
		MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory => Units.Percent(value, provider),
		MetricKind.Network => Units.BitsPerSecond(value, provider),
		MetricKind.Claude => Units.TokensPerMinute(value, provider),
		MetricKind.Process => Units.Percent(value, provider),
		MetricKind.Git => double.IsFinite(value) ? Units.Number(value, provider) + " lines" : Units.Unavailable,
		MetricKind.Counter => (channel.Watch?.Counter?.Unit ?? CounterUnit.Number) switch
		{
			CounterUnit.Percent => double.IsFinite(value) ? Units.Number(value, provider) + "%" : Units.Unavailable,
			CounterUnit.BytesPerSecond => Units.BytesPerSecond(value, provider),
			CounterUnit.Bytes => Units.Bytes(value, provider),
			CounterUnit.PerSecond => double.IsFinite(value) ? Units.Number(value, provider) + "/s" : Units.Unavailable,
			_ => Units.Number(value, provider),
		},
		_ => Units.BytesPerSecond(value, provider),
	};

	/// <summary>"2 processes · 1.24 GB · 84 threads · 1,204 handles", or "not running".</summary>
	private static ChannelReadout DescribeProcess(ChannelReadout readout, Channel channel, WatchReading reading, bool showStats, string scale)
	{
		var culture = CultureInfo.CurrentCulture;
		var detail = reading.Count == 0
			? "not running"
			: Join(
				reading.Count > 1 ? $"{reading.Count} processes" : null,
				Units.Bytes(reading.MemoryBytes),
				double.IsFinite(reading.Threads) ? $"{reading.Threads.ToString("N0", culture)} threads" : null,
				double.IsFinite(reading.Handles) ? $"{reading.Handles.ToString("N0", culture)} handles" : null);
		return readout with
		{
			PrimaryValue = Units.Percent(reading.Value),
			SecondaryValue = Units.Percent(reading.MemoryPercent),
			Detail = detail,
			Stats = showStats ? MinMax(channel.PrimaryRange, Units.Percent) : string.Empty,
			Scale = scale,
		};
	}

	/// <summary>"Web Service(_Total)", with the instances added up and the machine when it is not this one.</summary>
	private static ChannelReadout DescribeCounter(ChannelReadout readout, Channel channel, WatchReading reading, SystemInfo info, bool showStats, string scale)
	{
		var path = channel.Watch?.Counter;
		var machine = path?.Machine ?? info.Host;
		var detail = Join(
			path?.Source,
			path?.IsWildcard == true ? $"{reading.Count} instances added up" : null,
			machine is null ? null : $"on {machine}");
		return readout with
		{
			PrimaryValue = Format(channel, reading.Value),
			Detail = detail,
			Stats = showStats ? MinMax(channel.PrimaryRange, (value, provider) => Format(channel, value, provider)) : string.Empty,
			Scale = scale,
		};
	}

	/// <summary>
	/// "Webshop feature/x · Pulse main rebasing": every repository with its branch and what is in progress; the value
	/// turns red while an index.lock blocks git.
	/// </summary>
	private static ChannelReadout DescribeGit(ChannelReadout readout, Channel channel, MetricSnapshot sample, bool showStats, string scale)
	{
		var repositories = sample.Git?.Repositories ?? [];
		var shown = repositories.Where(repository => repository.IsShown).OrderByDescending(repository => repository.LastActivity).ToList();
		var active = repositories.Count(repository => repository.Active);
		var changed = repositories.Count(repository => repository.ChangedFiles > 0);
		var detail = repositories.Count == 0 ? "looking for repositories"
			: repositories.Count <= 2 && shown.Count == repositories.Count ? Join([.. shown.Select(Describe)])
			: Join(
				active > 0 ? $"{active} active" : null,
				changed > 0 ? $"{changed} with changes" : null,
				Count(repositories.Count, "repository", "repositories"));
		return readout with
		{
			PrimaryValue = Format(channel, sample.GitChangedLines),
			Detail = detail,
			Stats = showStats ? MinMax(channel.PrimaryRange, (value, provider) => Units.Number(value, provider)) : string.Empty,
			Scale = scale,
			Alert = repositories.Any(repository => repository.HasStaleLock),
		};
	}

	/// <summary>"Webshop feature/x rebasing".</summary>
	private static string Describe(GitRepositoryStatus repository)
		=> string.Join(' ', new[] { repository.Name, repository.Branch, repository.State }.Where(part => !string.IsNullOrEmpty(part)));

	private static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

	private static WatchReading Reading(Channel channel, MetricSnapshot sample)
		=> channel.WatchIndex >= 0 && channel.WatchIndex < sample.Watches.Count ? sample.Watches[channel.WatchIndex] : WatchReading.None;

	/// <summary>"16 threads"; another machine that did not say how many it has is counted by its per-core counters.</summary>
	private static string? Threads(SystemInfo info, MetricSnapshot? sample)
	{
		var threads = info.LogicalProcessors > 0 ? info.LogicalProcessors : sample?.CpuCorePercents.Length ?? 0;
		return threads > 0 ? $"{threads} threads" : null;
	}

	/// <summary>"2 sessions · claude-opus-5-5 · cache 94%": who is working, with what, and how well the cache helps.</summary>
	private static string ClaudeDetail(MetricSnapshot sample)
	{
		var sessions = sample.ClaudeSessions;
		var models = string.Join(", ", sessions.Select(session => session.Model).Where(model => model.Length > 0).Distinct());
		var total = sample.ClaudeTotal;
		var cache = total.AllInput > 0 ? $"cache {Units.Percent(100.0 * total.CacheRead / total.AllInput)}" : null;
		// Active: answered in the last 10 minutes; the panel counts the sessions that are open.
		var count = $"{sessions.Count} active";
		return Join(count, models, cache);
	}

	private static string InitialDetail(Channel channel, SystemInfo info) => channel.Kind switch
	{
		MetricKind.Cpu => Join(info.CpuName, Threads(info, null)),
		MetricKind.Gpu => info.GpuName ?? string.Empty,
		MetricKind.Network => info.NetworkAdapters ?? "no physical adapter is up",
		MetricKind.Claude => "waiting for the first answer",
		MetricKind.Counter => channel.Watch?.Counter?.Source ?? string.Empty,
		MetricKind.Git => info.GitRepositories.Count > 0 ? string.Join(" · ", info.GitRepositories) : "looking for repositories",
		_ => string.Empty,
	};

	private static string MinMax(ValueRange range, Func<double, IFormatProvider?, string> format)
		=> range.IsEmpty ? string.Empty : $"min {format(range.Min, null)}{Separator}max {format(range.Max, null)}";

	private static string Peaks(Channel channel, Func<double, IFormatProvider?, string> format)
	{
		var (primaryLabel, secondaryLabel) = ValueLabelsOf(channel.Kind);
		var primary = channel.PrimaryRange;
		var secondary = channel.SecondaryRange;
		if (primary.IsEmpty && secondary.IsEmpty)
		{
			return string.Empty;
		}

		return "peak " + Join(
			primary.IsEmpty ? null : $"{primaryLabel} {format(primary.Max, null)}",
			secondary.IsEmpty ? null : $"{secondaryLabel} {format(secondary.Max, null)}");
	}

	private static string? Optional(double value, Func<double, IFormatProvider?, string> format)
		=> double.IsFinite(value) ? format(value, null) : null;

	private static string Join(params string?[] parts)
		=> string.Join(Separator, parts.Where(part => !string.IsNullOrEmpty(part)));
}
