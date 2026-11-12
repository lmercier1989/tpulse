using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Metrics;

/// <summary>
/// Errors and critical events of the Windows System and Application logs as they are written: an application that
/// crashes or hangs, a disk or driver error, a service that fails. Every account may read these two logs. The same
/// error from the same source within <see cref="RepeatWindow"/> counts once, so a noisy service does not fill the axis.
/// </summary>
internal sealed class WindowsEventSource : IDisposable
{
	private static readonly string[] Logs = ["System", "Application"];
	private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);
	private const string ErrorsAndCritical = "*[System[(Level=1 or Level=2)]]";
	private const int TextLength = 160;

	private readonly List<EventLogWatcher> _watchers = [];
	private readonly ConcurrentQueue<(DateTimeOffset At, string Source, string Action, string Text)> _arrived = new();
	private readonly ConcurrentDictionary<(string Source, int Id), DateTimeOffset> _lastSeen = new();

	private WindowsEventSource()
	{
	}

	/// <summary>Starts following the logs; null when neither can be read.</summary>
	public static WindowsEventSource? TryCreate()
	{
		var source = new WindowsEventSource();
		foreach (var log in Logs)
		{
			try
			{
				var watcher = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, ErrorsAndCritical));
				watcher.EventRecordWritten += source.OnRecord;
				watcher.Enabled = true;
				source._watchers.Add(watcher);
			}
			catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
			{
				Log.Warn($"The Windows {log} log cannot be followed; its errors are not shown.", ex);
			}
		}

		if (source._watchers.Count == 0)
		{
			return null;
		}

		Log.Info($"Following errors in the Windows logs: {string.Join(", ", Logs)}.");
		return source;
	}

	/// <summary>The errors written since the last call, as events on the sample clock.</summary>
	public void Drain(double now, DateTimeOffset wallNow, List<PulseEvent> events)
	{
		while (_arrived.TryDequeue(out var item))
		{
			var time = now - Math.Max(0, (wallNow - item.At).TotalSeconds);
			events.Add(new PulseEvent(time, item.At, EventKind.Error, item.Source, item.Action, item.Text));
		}
	}

	public void Dispose()
	{
		foreach (var watcher in _watchers)
		{
			watcher.Enabled = false;
			watcher.Dispose();
		}
	}

	/// <summary>"Faulting application name: devenv.exe, ..." becomes its first line, cut to a panel row.</summary>
	internal static string FirstLine(string? description)
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

	private void OnRecord(object? sender, EventRecordWrittenEventArgs e)
	{
		using var record = e.EventRecord;
		if (record is null)
		{
			return;
		}

		try
		{
			var source = record.ProviderName ?? "Windows";
			var at = record.TimeCreated is { } created ? new DateTimeOffset(created) : TimeProvider.System.GetUtcNow();
			var key = (source, record.Id);
			if (_lastSeen.TryGetValue(key, out var previous) && at - previous < RepeatWindow)
			{
				return;
			}

			_lastSeen[key] = at;
			string? description = null;
			try
			{
				description = record.FormatDescription();
			}
			catch (EventLogException)
			{
				// No message file for this provider: the source and the id still say enough.
			}

			var text = FirstLine(description);
			_arrived.Enqueue((at, source, record.Level == 1 ? "critical" : "error", text.Length > 0 ? text : $"event {record.Id}"));
		}
		catch (EventLogException ex)
		{
			Log.WarnOnce("eventlog-read", "Reading an event from the Windows log failed.", ex);
		}
	}
}
