using System.Threading;

using Pulse.Core.Diagnostics;

namespace Pulse;

/// <summary>
/// Windows notifications for alerts, shown through a notification-area icon: Windows 10 and 11 turn its balloon into a
/// regular notification, which needs no app registration. The icon runs on a thread of its own with a message loop,
/// so it works the same in the terminal and in the window. The icon stays in the notification area while
/// notifications are on.
/// </summary>
internal sealed class Notifier : IDisposable
{
	private readonly ManualResetEventSlim _ready = new();
	private System.Windows.Forms.Control? _invoker;
	private System.Windows.Forms.NotifyIcon? _icon;
	private bool _disposed;

	public Notifier()
	{
		var thread = new Thread(Run) { IsBackground = true, Name = "Pulse notifications" };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		_ready.Wait(TimeSpan.FromSeconds(5));
	}

	/// <summary>Shows a notification; does nothing when the icon could not be created.</summary>
	public void Show(string title, string text)
	{
		if (_disposed || _invoker is not { IsHandleCreated: true } invoker)
		{
			return;
		}

		invoker.BeginInvoke(() => _icon?.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Warning));
		Log.Info($"Notification: {title}. {text}");
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		if (_invoker is { IsHandleCreated: true } invoker)
		{
			invoker.BeginInvoke(() =>
			{
				if (_icon is not null)
				{
					_icon.Visible = false;
					_icon.Dispose();
				}

				System.Windows.Forms.Application.ExitThread();
			});
		}

		_ready.Dispose();
	}

	private void Run()
	{
		try
		{
			_invoker = new System.Windows.Forms.Control();
			_invoker.CreateControl();
			_ = _invoker.Handle;
			_icon = new System.Windows.Forms.NotifyIcon
			{
				Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "tpulse.exe"),
				Text = "Pulse",
				Visible = true,
			};
		}
		catch (Exception ex)
		{
			Log.Warn("The notification icon could not be created; notifications are off.", ex);
			_ready.Set();
			return;
		}

		_ready.Set();
		System.Windows.Forms.Application.Run();
	}
}
