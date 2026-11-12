using System.ComponentModel;
using System.Diagnostics;

using Pulse.Core.Diagnostics;

namespace Pulse;

/// <summary>
/// Desktop notifications for alerts on Linux, through notify-send (libnotify), which every desktop with a notification
/// daemon understands. Without notify-send, or without a desktop (over SSH), alerts stay in the monitor and the log.
/// </summary>
internal sealed class Notifier : IDisposable
{
	private bool _unavailable;

	/// <summary>Shows a notification; does nothing when notify-send cannot be started.</summary>
	public void Show(string title, string text)
	{
		Log.Info($"Notification: {title}. {text}");
		if (_unavailable)
		{
			return;
		}

		var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardError = true };
		foreach (var argument in new[] { "--app-name=Pulse", "--icon=dialog-warning", "--", title, text })
		{
			start.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(start);
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			_unavailable = true;
			Log.Warn("notify-send could not be started; notifications are shown in the monitor only.", ex);
		}
	}

	public void Dispose()
	{
	}
}
