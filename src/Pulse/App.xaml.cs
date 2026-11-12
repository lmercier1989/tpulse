using System.Windows;
using System.Windows.Threading;

using Pulse.Core.Diagnostics;
using Pulse.Core.Metrics;
using Pulse.Core.Settings;

namespace Pulse;

/// <summary>The graphical window mode (<c>tpulse -window</c>); <see cref="Program"/> prepares everything it needs.</summary>
public partial class App : Application
{
	private readonly SystemMetricsCollector _collector;
	private readonly SettingsStore _store;
	private readonly PulseSettings _saved;
	private readonly PulseSettings _settings;
	private readonly bool _layoutFromCommandLine;
	private bool _fatalErrorShown;

	public App(SystemMetricsCollector collector, SettingsStore store, PulseSettings saved, PulseSettings settings, bool layoutFromCommandLine)
	{
		_collector = collector;
		_store = store;
		_saved = saved;
		_settings = settings;
		_layoutFromCommandLine = layoutFromCommandLine;
		ShutdownMode = ShutdownMode.OnMainWindowClose;
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		var window = new MainWindow(_collector, _store, _saved, _settings, _layoutFromCommandLine);
		MainWindow = window;
		window.Show();
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		Log.Error("Unhandled exception on the UI thread; Pulse closes.", e.Exception);
		e.Handled = true;
		if (_fatalErrorShown)
		{
			return;
		}

		_fatalErrorShown = true;
		MessageBox.Show(
			$"Pulse hit an unexpected error and closes.{Environment.NewLine}{Environment.NewLine}{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}Log: {Log.FilePath}",
			"Pulse",
			MessageBoxButton.OK,
			MessageBoxImage.Error);
		Shutdown(1);
	}
}
