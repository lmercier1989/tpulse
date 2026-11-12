using Pulse.Core.Formatting;
using Pulse.Core.Metrics;
using Pulse.Core.Processing;

namespace Pulse.Core.Presentation;

/// <summary>A Windows notification Pulse shows when something needs attention.</summary>
public sealed record Alert(string Key, string Title, string Text);

/// <summary>
/// Decides when Pulse notifies, also while its window or tab is not in sight: CPU, GPU or memory that stays at 90 % or
/// more (the moment its value turns red), a disk that fills past 90 %, an index.lock that blocks git, and a critical
/// Windows event. An alert is raised when it starts; one that comes back within <see cref="RepeatAfter"/> stays quiet.
/// </summary>
public sealed class AlertMonitor
{
	public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(5);

	private const double DiskFullPercent = 90;

	private readonly Dictionary<string, DateTimeOffset> _raised = [];
	private readonly HashSet<string> _active = [];

	/// <summary>The alerts that start (or repeat) with <paramref name="sample"/>.</summary>
	/// <param name="channels">The graphs; CPU, GPU and memory are checked for a lasting load.</param>
	/// <param name="sample">The newest sample.</param>
	/// <param name="now">The wall clock.</param>
	public List<Alert> Check(IEnumerable<Channel> channels, MetricSnapshot sample, DateTimeOffset now)
	{
		var current = new List<Alert>();
		foreach (var channel in channels)
		{
			if (channel.Kind is MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory
				&& channel.IsSustainedAbove(ChannelText.AlertPercent, ChannelText.AlertSeconds, sample.Time))
			{
				var title = ChannelText.TitleOf(channel.Kind);
				current.Add(new Alert(
					$"load-{channel.Kind}",
					$"{title} at {Units.Percent(channel.Primary.Latest)}",
					$"{title} has been at {ChannelText.AlertPercent:0}% or more for {ChannelText.AlertSeconds:0} seconds."));
			}
		}

		foreach (var disk in sample.Details?.Disks ?? [])
		{
			if (disk.UsedPercent >= DiskFullPercent)
			{
				current.Add(new Alert($"disk-{disk.Name}", $"Disk {disk.Name} {Units.Percent(disk.UsedPercent)} full", $"{Units.Bytes(disk.FreeBytes)} free of {Units.Bytes(disk.TotalBytes)}."));
			}
		}

		foreach (var repository in sample.Git?.Repositories ?? [])
		{
			if (repository.HasStaleLock)
			{
				current.Add(new Alert(
					$"lock-{repository.Root}",
					$"git is blocked in {repository.Name}",
					$"index.lock has existed for {InfoPanel.FormatAgo(repository.IndexLockAge!.Value)}: a git process crashed or hangs. Delete it when no git runs."));
			}
		}

		foreach (var item in sample.Events)
		{
			if (item.Kind == EventKind.Error && item.Action == "critical")
			{
				current.Add(new Alert($"event-{item.Source}-{item.Text}", $"Critical: {item.Source}", item.Text));
			}
			else if (item.Kind == EventKind.Leak)
			{
				current.Add(new Alert($"leak-{item.Text}", "Memory keeps growing", item.Text));
			}
			else if (item.Kind == EventKind.Mark && item.Action is "done" or "failed")
			{
				current.Add(new Alert($"run-{item.At.UtcTicks}-{item.Text}", item.Action == "done" ? $"Done in {item.Source}" : $"Failed in {item.Source}", item.Text));
			}
		}

		var raise = new List<Alert>();
		_active.IntersectWith(current.Select(alert => alert.Key));
		foreach (var alert in current)
		{
			var started = _active.Add(alert.Key);
			var due = !_raised.TryGetValue(alert.Key, out var last) || now - last >= RepeatAfter;
			if (started && due)
			{
				_raised[alert.Key] = now;
				raise.Add(alert);
			}
		}

		return raise;
	}
}
