using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Pulse.Core.Diagnostics;
using Pulse.Core.Metrics;
using Pulse.Core.Settings;

namespace Pulse;

/// <summary>The graphical window mode (<c>tpulse -window</c>); <see cref="Program"/> prepares everything it needs.</summary>
public partial class App : Application
{
	/// <summary>The metric sources; the window disposes them when it closes.</summary>
	public SystemMetricsCollector? Collector { get; init; }

	public SettingsStore? Store { get; init; }

	public PulseSettings? Saved { get; init; }

	public PulseSettings? Settings { get; init; }

	public bool LayoutFromCommandLine { get; init; }

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		Dispatcher.UIThread.UnhandledException += (_, e) =>
		{
			// No message box in Avalonia itself: the log keeps the details, and the window closes rather than run on broken.
			Log.Error("Unhandled exception on the UI thread; Pulse closes.", e.Exception);
			e.Handled = true;
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
			{
				lifetime.Shutdown(1);
			}
		};

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Collector is not null)
		{
			desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
			desktop.MainWindow = new MainWindow(Collector, Store!, Saved!, Settings!, LayoutFromCommandLine);
		}

		base.OnFrameworkInitializationCompleted();
	}
}
