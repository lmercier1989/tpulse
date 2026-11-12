using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Metrics;

/// <summary>
/// Errors and critical messages of the systemd journal as they are written: a program that crashes, a disk or driver
/// error, a unit that fails. They are read from <c>journalctl --follow</c>, which shows what this account may see (the
/// whole system for members of the wheel, adm or systemd-journal group, the own messages otherwise). The same message
/// from the same source within <see cref="RepeatWindow"/> counts once, so a noisy service does not fill the axis.
/// </summary>
internal sealed class JournalEventSource : IDisposable
{
	private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

	private readonly Process _journal;
	private readonly ConcurrentQueue<(DateTimeOffset At, string Source, string Action, string Text)> _arrived = new();
	private readonly Dictionary<(string Source, string Text), DateTimeOffset> _lastSeen = [];
	private readonly Task _reader;
	private bool _disposed;

	private JournalEventSource(Process journal)
	{
		_journal = journal;
		_reader = Task.Run(ReadAsync);
	}

	/// <summary>Starts following the journal; null when journalctl is not there (no systemd).</summary>
	public static JournalEventSource? TryCreate()
	{
		var start = new ProcessStartInfo("journalctl")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
		};
		foreach (var argument in new[] { "--follow", "--lines=0", "--priority=3", "--output=json", "--no-pager", "--quiet" })
		{
			start.ArgumentList.Add(argument);
		}

		try
		{
			var process = Process.Start(start);
			if (process is null)
			{
				return null;
			}

			process.ErrorDataReceived += (_, _) => { };
			process.BeginErrorReadLine();
			Log.Info("Following errors in the systemd journal.");
			return new JournalEventSource(process);
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			Log.Warn("The systemd journal cannot be followed; its errors are not shown.", ex);
			return null;
		}
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
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			if (!_journal.HasExited)
			{
				_journal.Kill();
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
		{
			// Ended already.
		}

		_ = _reader.Wait(TimeSpan.FromSeconds(1));
		_journal.Dispose();
	}

	/// <summary>
	/// One line of <c>journalctl --output=json</c> as an event: when, who, how bad and what; null for a line that is not a
	/// message. MESSAGE is a byte array instead of a string when it is not valid UTF-8.
	/// </summary>
	internal static (DateTimeOffset At, string Source, string Action, string Text)? Parse(string line)
	{
		using var document = JsonDocument.Parse(line);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("MESSAGE", out var message))
		{
			return null;
		}

		var text = message.ValueKind switch
		{
			JsonValueKind.String => message.GetString(),
			JsonValueKind.Array => System.Text.Encoding.UTF8.GetString([.. message.EnumerateArray().Select(b => b.TryGetByte(out var value) ? value : (byte) '?')]),
			_ => null,
		};
		var source = String(root, "SYSLOG_IDENTIFIER") ?? String(root, "_COMM") ?? String(root, "_SYSTEMD_UNIT") ?? "journal";
		var priority = int.TryParse(String(root, "PRIORITY"), NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : 3;
		var at = long.TryParse(String(root, "__REALTIME_TIMESTAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds)
			? DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000)
			: TimeProvider.System.GetUtcNow();
		var first = EventText.FirstLine(text);
		return (at, source, priority <= 2 ? "critical" : "error", first.Length > 0 ? first : "error");

		static string? String(JsonElement root, string name)
			=> root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	}

	private async Task ReadAsync()
	{
		try
		{
			while (await _journal.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				try
				{
					if (Parse(line) is not { } item)
					{
						continue;
					}

					var key = (item.Source, item.Text);
					if (_lastSeen.TryGetValue(key, out var previous) && item.At - previous < RepeatWindow)
					{
						continue;
					}

					if (_lastSeen.Count > 1000)
					{
						_lastSeen.Clear();
					}

					_lastSeen[key] = item.At;
					_arrived.Enqueue(item);
				}
				catch (JsonException ex)
				{
					Log.WarnOnce("journal-read", "A line of the systemd journal could not be read.", ex);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
		{
			// journalctl ended (Dispose) or its pipe closed.
		}
	}
}
