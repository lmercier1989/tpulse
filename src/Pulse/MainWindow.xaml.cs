using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using Pulse.Core.Diagnostics;
using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Settings;
using Pulse.Interop;
using Pulse.Rendering;

namespace Pulse;

/// <summary>
/// The instrument window: stacks the graphs, owns the sampling loop and the render loop, and handles the
/// keyboard, mouse and context menu. Samples arrive on a pool thread and are applied on the UI thread;
/// frames are driven by <see cref="CompositionTarget.Rendering"/> and stop while paused or minimized.
/// </summary>
public partial class MainWindow : Window
{
	private readonly SystemMetricsCollector _collector;
	private readonly SettingsStore _store;
	private readonly PulseSettings _saved;
	private readonly PulseSettings _settings;
	private readonly bool _layoutFromCommandLine;
	private readonly IReadOnlyList<Graph> _graphs;
	private readonly ChannelPanel[] _panels;
	private readonly IReadOnlyList<KeyGroup> _keys;
	private readonly Sampler _sampler;
	private readonly DispatcherTimer _toastTimer;
	private readonly EventHistory _events = new();
	private readonly Core.Collections.RingBuffer<MetricSnapshot> _history = new(Channel.Capacity);
	private readonly AlertMonitor _alerts = new();
	private Notifier? _notifier;
	private double _viewEnd = double.NaN;
	private MetricSnapshot? _lastSample;
	private bool _started;
	private bool _rendering;
	private bool _paused;
	private bool _closing;
	private double _frozenTime;
	private double _lastFrame;
	private TimeSpan _lastRenderingTime;

	/// <param name="collector">The metric sources; disposed when the window closes.</param>
	/// <param name="store">Where the remembered settings are written on close.</param>
	/// <param name="saved">The remembered settings; changes made in the window are applied here too.</param>
	/// <param name="settings">The settings of this launch: the remembered ones plus the command-line options.</param>
	/// <param name="layoutFromCommandLine">The command line chose the graphs or the compact layout.</param>
	public MainWindow(SystemMetricsCollector collector, SettingsStore store, PulseSettings saved, PulseSettings settings, bool layoutFromCommandLine)
	{
		InitializeComponent();
		_collector = collector;
		_store = store;
		_saved = saved;
		_settings = settings;
		_layoutFromCommandLine = layoutFromCommandLine;

		Icon = AppIcon.Create();
		Topmost = settings.AlwaysOnTop;
		if (collector.Info.Host is { } host)
		{
			Title = $"Pulse · {host}";
		}

		_graphs = Graph.CreateAll(settings, saved, collector.Info);
		_panels = [.. _graphs.Select(CreatePanel)];
		for (var i = 0; i < _panels.Length; i++)
		{
			_panels[i].Scope.ShowEvents(_events);
			AttachTooltip(_panels[i], _graphs[i]);
		}

		if (settings.Notify)
		{
			_notifier = new Notifier();
		}
		_keys = KeyMap.For(Surface.Window, [.. _graphs.Skip(MetricKinds.Builtin.Count).Select(graph => ChannelText.TitleOf(graph.Channel))]);
		_sampler = new Sampler(collector, TimeSpan.FromSeconds(settings.IntervalSeconds), OnSampleCollected);
		_toastTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1.8) };
		_toastTimer.Tick += (_, _) => HideToast();

		BuildHelp();
		ApplyTiming();
		BuildLayout();
		RestoreWindowBounds();

		Root.ContextMenu = new ContextMenu();
		Root.ContextMenuOpening += OnContextMenuOpening;
		HelpOverlay.MouseLeftButtonUp += (_, _) => SetHelpVisible(false);
		SizeChanged += (_, _) => RenderStillFrameLater();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		DwmStyling.Apply(this);
	}

	protected override void OnContentRendered(EventArgs e)
	{
		base.OnContentRendered(e);
		if (_started)
		{
			return;
		}

		_started = true;
		SizeToContent = SizeToContent.Manual;
		_sampler.Start();
		StartRendering();
		if (_saved.Window is null)
		{
			ShowToast("F1 help");
		}

		Log.Info($"Window open: history {_settings.HistorySeconds} s, interval {_settings.IntervalSeconds} s, {_settings.TargetFps} fps.");
	}

	protected override void OnStateChanged(EventArgs e)
	{
		base.OnStateChanged(e);
		if (WindowState == WindowState.Minimized)
		{
			StopRendering();
		}
		else
		{
			StartRendering();
		}
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		base.OnPreviewKeyDown(e);
		if (!e.Handled)
		{
			e.Handled = HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
		}
	}

	/// <summary>The mouse wheel scrolls through the history, like the arrow keys: away from you goes back in time.</summary>
	protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
	{
		base.OnPreviewMouseWheel(e);
		// Over the overview the wheel scrolls the overview.
		if (!e.Handled && e.Delta != 0 && HelpOverlay.Visibility != Visibility.Visible)
		{
			Scroll(Math.Sign(e.Delta) * _settings.HistorySeconds / 8);
			e.Handled = true;
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		base.OnClosing(e);
		if (e.Cancel)
		{
			return;
		}

		_closing = true;
		SaveWindowBounds();
		_store.Save(_saved);
	}

	protected override void OnClosed(EventArgs e)
	{
		StopRendering();
		_toastTimer.Stop();
		_sampler.Dispose();
		_collector.Dispose();
		_notifier?.Dispose();
		base.OnClosed(e);
	}

	private static string FormatDuration(double seconds) => PulseSettings.FormatDuration(seconds);

	private static int? DigitIndex(Key key) => key switch
	{
		>= Key.D1 and <= Key.D9 => key - Key.D1,
		>= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
		_ => null,
	};

	private static MenuItem MenuEntry(string header, string? gesture, Action action, bool isChecked = false)
	{
		var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, IsChecked = isChecked };
		item.Click += (_, _) => action();
		return item;
	}

	private ChannelPanel CreatePanel(Graph graph)
	{
		var panel = new ChannelPanel(graph.Kind, graph.Channel, ScopeStyle.For(graph.Kind), _collector.Info);
		panel.HeaderClicked += (_, _) => SetSize(graph, graph.Size == ChannelSize.Collapsed ? ChannelSize.Normal : ChannelSize.Collapsed);
		panel.PlotDoubleClicked += (_, _) => SetSize(graph, graph.Size == ChannelSize.Tall ? ChannelSize.Normal : ChannelSize.Tall);
		return panel;
	}

	// ---- Samples ---------------------------------------------------------------------------------------

	private void OnSampleCollected(MetricSnapshot snapshot)
	{
		if (!Dispatcher.HasShutdownStarted)
		{
			Dispatcher.InvokeAsync(() => OnSample(snapshot), DispatcherPriority.Normal);
		}
	}

	private void OnSample(MetricSnapshot sample)
	{
		if (_closing || _paused)
		{
			return;
		}

		_lastSample = sample;
		_history.Add(sample);
		_events.Add(sample.Events);
		foreach (var graph in _graphs)
		{
			graph.Channel.Append(sample);
		}

		if (_notifier is not null)
		{
			foreach (var alert in _alerts.Check(_graphs.Select(graph => graph.Channel), sample, TimeProvider.System.GetUtcNow()))
			{
				_notifier.Show(alert.Title, alert.Text);
			}
		}

		RefreshChannels();
		PresentAll();
	}

	private void RefreshChannels()
	{
		if (_lastSample is null)
		{
			return;
		}

		var newest = _paused || Scrolled ? ViewTime() : _lastSample.Time;
		foreach (var graph in _graphs)
		{
			graph.Channel.Refresh(newest, _settings.HistorySeconds, _settings.DisplayDelaySeconds);
		}
	}

	/// <summary>Whether the view is held at an earlier moment by scrolling; sampling goes on meanwhile.</summary>
	private bool Scrolled => !double.IsNaN(_viewEnd);

	/// <summary>The newest moment the view can show: now, or the moment of the pause.</summary>
	private double EdgeTime() => _paused ? _frozenTime : MonotonicClock.Now;

	/// <summary>The clock of the right edge plus the display delay: the scrolled-to moment, or else the edge.</summary>
	private double ViewTime() => Scrolled ? _viewEnd : EdgeTime();

	/// <summary>The sample at the right edge of the view, for the values in the headers.</summary>
	private MetricSnapshot? ViewSample()
	{
		if (!Scrolled || _history.Count == 0)
		{
			return _lastSample;
		}

		var edge = ViewTime() - _settings.DisplayDelaySeconds;
		for (var i = _history.Count - 1; i >= 0; i--)
		{
			if (_history[i].Time <= edge)
			{
				return _history[i];
			}
		}

		return _history[0];
	}

	/// <summary>The newest sample at or before <paramref name="time"/>.</summary>
	private MetricSnapshot? SampleAt(double time)
	{
		for (var i = _history.Count - 1; i >= 0; i--)
		{
			if (_history[i].Time <= time)
			{
				return _history[i];
			}
		}

		return _history.Count > 0 ? _history[0] : null;
	}

	/// <summary>
	/// A tooltip over every graph that follows the mouse: the moment under it, the values of that graph then, the busiest
	/// processes of that moment and the events around it. Holding the left button down freezes it where it is, so it can
	/// be read at leisure; letting go makes it follow the mouse again. A popup of its own rather than a WPF tooltip,
	/// because the tooltip service closes a tooltip on every mouse down.
	/// </summary>
	private void AttachTooltip(ChannelPanel panel, Graph graph)
	{
		var scope = panel.Scope;
		var text = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE)) };
		var held = new TextBlock { FontFamily = text.FontFamily, FontSize = 11, Foreground = new SolidColorBrush(Palette.TitleText), Margin = new Thickness(0, 6, 0, 0), Text = "held · let go to follow the mouse", Visibility = Visibility.Collapsed };
		var tip = new System.Windows.Controls.Primitives.Popup
		{
			Child = new Border
			{
				Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1B)),
				BorderBrush = new SolidColorBrush(Palette.WindowBorder),
				BorderThickness = new Thickness(1),
				Padding = new Thickness(10, 8, 10, 8),
				Child = new StackPanel { Children = { text, held } },
				IsHitTestVisible = false,
			},
			PlacementTarget = scope,
			Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
			AllowsTransparency = true,
		};
		var frozen = false;

		void Follow(Point point)
		{
			text.Text = TooltipText(graph, point.X / Math.Max(1, scope.ActualWidth));
			tip.HorizontalOffset = point.X + 16;
			tip.VerticalOffset = point.Y + 16;
			tip.IsOpen = true;
		}

		void Release(Point point)
		{
			frozen = false;
			held.Visibility = Visibility.Collapsed;
			if (point.X >= 0 && point.Y >= 0 && point.X <= scope.ActualWidth && point.Y <= scope.ActualHeight)
			{
				Follow(point);
			}
			else
			{
				tip.IsOpen = false;
			}
		}

		scope.MouseMove += (_, e) =>
		{
			if (!frozen)
			{
				Follow(e.GetPosition(scope));
			}
		};
		scope.MouseLeave += (_, _) =>
		{
			if (!frozen)
			{
				tip.IsOpen = false;
			}
		};
		scope.MouseLeftButtonDown += (_, e) =>
		{
			if (e.ClickCount == 1 && tip.IsOpen && scope.CaptureMouse())
			{
				frozen = true;
				held.Visibility = Visibility.Visible;
			}
		};
		scope.MouseLeftButtonUp += (_, e) =>
		{
			if (frozen)
			{
				scope.ReleaseMouseCapture();
			}
		};
		scope.LostMouseCapture += (_, _) =>
		{
			if (frozen)
			{
				Release(Mouse.GetPosition(scope));
			}
		};
		Deactivated += (_, _) =>
		{
			scope.ReleaseMouseCapture();
			tip.IsOpen = false;
		};
	}

	/// <summary>"CPU 63% claude 12%", then the moment as the terminal's flyout shows it: busiest then, events, in view.</summary>
	private string TooltipText(Graph graph, double fraction)
	{
		var now = _paused || Scrolled ? ViewTime() : MonotonicClock.Now;
		var time = now - _settings.DisplayDelaySeconds - ((1 - fraction) * _settings.HistorySeconds);
		if (SampleAt(time) is not { } sample)
		{
			return "No samples yet";
		}

		// The values of this graph, then the same rows as the flyout of the terminal: busiest then, events, in view.
		var readout = ChannelText.Describe(graph.Channel, _collector.Info, sample, showStats: false);
		var at = TimeProvider.System.GetLocalNow() - TimeSpan.FromSeconds(Math.Max(0, MonotonicClock.Now - sample.Time));
		var text = new System.Text.StringBuilder();
		text.Append(readout.Title.ToUpperInvariant()).Append("  ").Append(Labelled(readout.PrimaryLabel, readout.PrimaryValue));
		if (readout.HasSecondary)
		{
			text.Append("  ").Append(Labelled(readout.SecondaryLabel, readout.SecondaryValue));
		}

		text.AppendLine();
		if (readout.Detail.Length > 0)
		{
			text.AppendLine(readout.Detail);
		}

		var right = now - _settings.DisplayDelaySeconds;
		var inView = new List<MetricSnapshot>();
		for (var i = 0; i < _history.Count; i++)
		{
			if (_history[i].Time >= right - _settings.HistorySeconds && _history[i].Time <= right)
			{
				inView.Add(_history[i]);
			}
		}

		var focus = new PanelFocus(at, Pinned: false, [.. _events.Between(sample.Time - 10, sample.Time + 10)], ProcessCausation.AverageCpu(inView, 5));
		text.AppendLine();
		foreach (var line in PanelText.Render(InfoPanel.BuildFocus(sample, focus), 44))
		{
			text.AppendLine(line);
		}

		return text.ToString().TrimEnd();

		static string Labelled(string label, string value) => label.Length > 0 ? $"{label} {value}" : value;
	}

	/// <summary>
	/// Moves the view back (positive) or forward (negative) and holds it there while sampling goes on; it stops at the
	/// oldest sample, and reaching the newest moment follows it live again.
	/// </summary>
	private void Scroll(double seconds)
	{
		var edge = EdgeTime();
		var oldest = _history.Count == 0 ? edge : _history[0].Time + _settings.DisplayDelaySeconds + _settings.HistorySeconds;
		var end = Math.Max(ViewTime() - seconds, Math.Min(oldest, edge));
		_viewEnd = end >= edge - 0.05 ? double.NaN : end;
		RefreshChannels();
		PresentAll();
		RenderStillFrameLater();
		if (Scrolled)
		{
			ShowToast(Messages.Back(edge - _viewEnd), sticky: true);
		}
		else
		{
			ShowToast(_paused ? Messages.Paused : Messages.Resumed, sticky: _paused);
		}
	}

	private void PresentAll()
	{
		if (_lastSample is null)
		{
			return;
		}

		var showStats = _settings.ShowMinMax && !_settings.Compact;
		var sample = ViewSample() ?? _lastSample;
		for (var i = 0; i < _panels.Length; i++)
		{
			if (_graphs[i].Visible)
			{
				_panels[i].Present(sample, showStats);
			}
		}
	}

	// ---- Rendering -------------------------------------------------------------------------------------

	private void StartRendering()
	{
		if (_rendering || !_started || _paused || _closing || WindowState == WindowState.Minimized)
		{
			return;
		}

		_rendering = true;
		_lastFrame = MonotonicClock.Now;
		CompositionTarget.Rendering += OnRendering;
	}

	private void StopRendering()
	{
		if (!_rendering)
		{
			return;
		}

		_rendering = false;
		CompositionTarget.Rendering -= OnRendering;
	}

	private void OnRendering(object? sender, EventArgs e)
	{
		// Rendering can fire more than once per frame; one update per presented frame is enough.
		if (e is RenderingEventArgs args)
		{
			if (args.RenderingTime == _lastRenderingTime)
			{
				return;
			}

			_lastRenderingTime = args.RenderingTime;
		}

		// Skipping whole vsync intervals keeps the frame pacing even on 120/144 Hz displays.
		var now = MonotonicClock.Now;
		var elapsed = now - _lastFrame;
		if (elapsed < 0.75 / _settings.TargetFps)
		{
			return;
		}

		_lastFrame = now;
		TickPanels(Scrolled ? ViewTime() : now, Math.Min(elapsed, 0.25));
	}

	private void TickPanels(double now, double deltaSeconds)
	{
		for (var i = 0; i < _panels.Length; i++)
		{
			if (_panels[i].IsPlotVisible && _graphs[i].Visible)
			{
				_panels[i].Scope.Tick(now, deltaSeconds);
			}
		}
	}

	/// <summary>While the render loop is stopped (paused), redraw once after a change so the frozen picture stays correct.</summary>
	private void RenderStillFrameLater()
	{
		if (_rendering || !_started)
		{
			return;
		}

		Dispatcher.InvokeAsync(
			() =>
			{
				if (!_rendering && !_closing)
				{
					TickPanels(_paused || Scrolled ? ViewTime() : MonotonicClock.Now, 1);
				}
			},
			DispatcherPriority.Background);
	}

	private void ApplyTiming()
	{
		foreach (var panel in _panels)
		{
			panel.Scope.Configure(_settings.HistorySeconds, _settings.DisplayDelaySeconds, _settings.GapSeconds, _settings.ShowCores, _settings.ShowMarkers);
		}
	}

	// ---- Layout ----------------------------------------------------------------------------------------

	private void BuildLayout()
	{
		ChannelHost.Children.Clear();
		ChannelHost.RowDefinitions.Clear();
		ChannelHost.Margin = _settings.Compact ? new Thickness(10, 6, 10, 8) : new Thickness(16, 10, 16, 14);
		var spacing = _settings.Compact ? 6.0 : 12.0;

		var visible = Graph.DisplayOrder(_graphs, _settings.GraphOrder).Where(index => _graphs[index].Visible).ToList();
		for (var row = 0; row < visible.Count; row++)
		{
			var graph = _graphs[visible[row]];
			var panel = _panels[visible[row]];
			panel.ApplyLayout(graph.Size, _settings.Compact);
			ChannelHost.RowDefinitions.Add(new RowDefinition
			{
				Height = graph.Size == ChannelSize.Collapsed ? GridLength.Auto : new GridLength(panel.DesignedHeight, GridUnitType.Star),
			});
			panel.Margin = new Thickness(0, 0, 0, row < visible.Count - 1 ? spacing : 0);
			Grid.SetRow(panel, row);
			ChannelHost.Children.Add(panel);
		}

		EmptyHint.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		PresentAll();
		RenderStillFrameLater();
	}

	/// <summary>Resizes the window height to the designed graph heights, keeping its width and position.</summary>
	private void FitHeight()
	{
		if (WindowState != WindowState.Normal)
		{
			return;
		}

		SizeToContent = SizeToContent.Height;
		Dispatcher.InvokeAsync(() => SizeToContent = SizeToContent.Manual, DispatcherPriority.Loaded);
	}

	private void RestoreWindowBounds()
	{
		if (_settings.Window is not { } bounds)
		{
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			return;
		}

		var desktop = new Rect(
			SystemParameters.VirtualScreenLeft,
			SystemParameters.VirtualScreenTop,
			SystemParameters.VirtualScreenWidth,
			SystemParameters.VirtualScreenHeight);
		if (!desktop.IntersectsWith(new Rect(bounds.Left, bounds.Top, bounds.Width, 32)))
		{
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			return;
		}

		WindowStartupLocation = WindowStartupLocation.Manual;
		Left = bounds.Left;
		Top = bounds.Top;
		Width = Math.Max(bounds.Width, MinWidth);
		if (!_layoutFromCommandLine && bounds.Height > 0)
		{
			SizeToContent = SizeToContent.Manual;
			Height = Math.Max(bounds.Height, MinHeight);
		}

		if (bounds.Maximized)
		{
			WindowState = WindowState.Maximized;
		}
	}

	private void SaveWindowBounds()
	{
		var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
		if (bounds.IsEmpty)
		{
			return;
		}

		// A height chosen for graphs picked on the command line does not suit the remembered layout.
		var height = _layoutFromCommandLine ? _saved.Window?.Height ?? 0 : bounds.Height;
		_saved.Window = new WindowBounds
		{
			Left = bounds.Left,
			Top = bounds.Top,
			Width = bounds.Width,
			Height = height,
			Maximized = WindowState == WindowState.Maximized,
		};
	}

	// ---- Commands --------------------------------------------------------------------------------------

	private bool HandleKey(Key key, ModifierKeys modifiers)
	{
		if (HelpOverlay.Visibility == Visibility.Visible && key is Key.Escape or Key.F1)
		{
			SetHelpVisible(false);
			return true;
		}

		if (modifiers.HasFlag(ModifierKeys.Control))
		{
			if (key is Key.Q or Key.W)
			{
				Close();
				return true;
			}

			return false;
		}

		if (modifiers.HasFlag(ModifierKeys.Alt))
		{
			return false;
		}

		if (DigitIndex(key) is { } index && index < _graphs.Count)
		{
			if (modifiers.HasFlag(ModifierKeys.Shift))
			{
				SetSize(_graphs[index], _graphs[index].NextSize());
			}
			else
			{
				ToggleVisible(_graphs[index]);
			}

			return true;
		}

		switch (key)
		{
			case Key.Space:
				TogglePause();
				return true;
			case Key.H:
				SetHistory(PulseSettings.NextPreset(PulseSettings.HistoryPresets, _settings.HistorySeconds));
				return true;
			case Key.R:
				SetInterval(PulseSettings.NextPreset(PulseSettings.IntervalPresets, _settings.IntervalSeconds));
				return true;
			case Key.K:
				ToggleCores();
				return true;
			case Key.P:
				ToggleMinMax();
				return true;
			case Key.C:
				ToggleCompact();
				return true;
			case Key.T:
				ToggleTopmost();
				return true;
			case Key.M:
				ToggleMarkers();
				return true;
			case Key.N:
				ToggleNotifications();
				return true;
			case Key.Left:
				Scroll(modifiers.HasFlag(ModifierKeys.Shift) ? _settings.HistorySeconds : _settings.HistorySeconds / 4);
				return true;
			case Key.Right:
				Scroll(-(modifiers.HasFlag(ModifierKeys.Shift) ? _settings.HistorySeconds : _settings.HistorySeconds / 4));
				return true;
			case Key.Home:
				Scroll(double.PositiveInfinity);
				return true;
			case Key.End:
				Scroll(double.NegativeInfinity);
				return true;
			case Key.F:
				ToggleFrameRate();
				return true;
			case Key.F1 or Key.OemQuestion:
				SetHelpVisible(HelpOverlay.Visibility != Visibility.Visible);
				return true;
			default:
				return false;
		}
	}

	/// <summary>Applies a change made in the window to this launch and to the remembered settings.</summary>
	private void Change(Action<PulseSettings> apply)
	{
		apply(_settings);
		apply(_saved);
	}

	private void TogglePause()
	{
		// Looking back: Space returns to live (and resumes, when paused too).
		if (Scrolled)
		{
			_viewEnd = double.NaN;
			if (!_paused)
			{
				RefreshChannels();
				PresentAll();
				ShowToast(Messages.Resumed);
				return;
			}
		}

		if (_paused)
		{
			_paused = false;
			var now = MonotonicClock.Now;
			foreach (var graph in _graphs)
			{
				graph.Channel.AppendBreak(now);
			}

			_sampler.IsPaused = false;
			StartRendering();
			ShowToast(Messages.Resumed);
			return;
		}

		_paused = true;
		_frozenTime = MonotonicClock.Now;
		_sampler.IsPaused = true;
		StopRendering();
		ShowToast(Messages.Paused, sticky: true);
	}

	private void ToggleVisible(Graph graph)
	{
		graph.SetVisible(!graph.Visible);
		BuildLayout();
		FitHeight();
	}

	private void SetSize(Graph graph, ChannelSize size)
	{
		graph.SetSize(size);
		BuildLayout();
		FitHeight();
	}

	private void SetHistory(double seconds)
	{
		Change(settings => settings.HistorySeconds = seconds);
		ApplyTiming();
		RefreshChannels();
		PresentAll();
		RenderStillFrameLater();
		ShowToast(Messages.History(seconds));
	}

	private void SetInterval(double seconds)
	{
		Change(settings => settings.IntervalSeconds = seconds);
		_sampler.Interval = TimeSpan.FromSeconds(seconds);
		ApplyTiming();
		RefreshChannels();
		RenderStillFrameLater();
		ShowToast(Messages.Sampling(seconds));
	}

	private void ToggleCores()
	{
		Change(settings => settings.ShowCores = !settings.ShowCores);
		ApplyTiming();
		RenderStillFrameLater();
		ShowToast(Messages.Switch("Per-core traces", _settings.ShowCores));
	}

	private void ToggleMinMax()
	{
		Change(settings => settings.ShowMinMax = !settings.ShowMinMax);
		PresentAll();
	}

	private void ToggleCompact()
	{
		Change(settings => settings.Compact = !settings.Compact);
		BuildLayout();
		FitHeight();
	}

	private void ToggleMarkers()
	{
		Change(settings => settings.ShowMarkers = !settings.ShowMarkers);
		ApplyTiming();
		RenderStillFrameLater();
		ShowToast(Messages.Switch("Markers", _settings.ShowMarkers));
	}

	private void ToggleNotifications()
	{
		Change(settings => settings.Notify = !settings.Notify);
		if (_settings.Notify)
		{
			_notifier ??= new Notifier();
			_notifier.Show("Pulse notifications are on", "Pulse tells you when a load stays high, a disk fills up or git is blocked.");
		}
		else
		{
			_notifier?.Dispose();
			_notifier = null;
		}

		ShowToast(Messages.Switch("Notifications", _settings.Notify));
	}

	private void ToggleTopmost()
	{
		Change(settings => settings.AlwaysOnTop = !settings.AlwaysOnTop);
		Topmost = _settings.AlwaysOnTop;
		ShowToast(Messages.Topmost(Topmost));
	}

	private void ToggleFrameRate()
	{
		Change(settings => settings.TargetFps = settings.TargetFps >= 60 ? 30 : 60);
		ShowToast(Messages.FrameRate(_settings.TargetFps));
	}

	// ---- Toast, help and menu ----------------------------------------------------------------------------

	private void ShowToast(string text, bool sticky = false)
	{
		ToastText.Text = text;
		_toastTimer.Stop();
		Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
		if (!sticky)
		{
			_toastTimer.Start();
		}
	}

	private void HideToast()
	{
		_toastTimer.Stop();
		Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)));
	}

	private void SetHelpVisible(bool visible) => HelpOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>The overview of <see cref="KeyMap"/>: one block per group, side by side, every marker in its own colour.</summary>
	private void BuildHelp()
	{
		foreach (var group in _keys)
		{
			var block = new StackPanel { Width = 360, Margin = new Thickness(0, 0, 32, 20) };
			block.Children.Add(new TextBlock { Text = group.Title.ToUpperInvariant(), Style = (Style) FindResource("ChannelNameText"), Margin = new Thickness(0, 0, 0, 6) });
			var grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			for (var row = 0; row < group.Rows.Count; row++)
			{
				var binding = group.Rows[row];
				grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				var keys = new TextBlock { Style = (Style) FindResource("ChannelNameText"), Margin = new Thickness(0, 2, 12, 2) };
				if (binding.Marker is { } kind)
				{
					keys.Inlines.Add(new System.Windows.Documents.Run("│ ") { Foreground = new SolidColorBrush(Palette.MarkerOf(kind)) });
				}

				keys.Inlines.Add(new System.Windows.Documents.Run(binding.Keys));
				var action = new TextBlock { Text = binding.Action, Style = (Style) FindResource("DetailText"), Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
				Grid.SetRow(keys, row);
				Grid.SetRow(action, row);
				Grid.SetColumn(action, 1);
				grid.Children.Add(keys);
				grid.Children.Add(action);
			}

			block.Children.Add(grid);
			HelpGroups.Children.Add(block);
		}

		HelpFooter.Text = "From a terminal: tpulse -help, or ? twice inside tpulse, lists every command.";
	}

	/// <summary>
	/// The first rows of the menu, as in the terminal: for the moment under the mouse (mark it, copy it) and for its graph
	/// (hide it, its size, its place, only it).
	/// </summary>
	private void AddTargetEntries(ContextMenu menu)
	{
		var index = Array.FindIndex(_panels, panel => panel.IsMouseOver);
		if (index < 0)
		{
			return;
		}

		var graph = _graphs[index];
		var title = ChannelText.TitleOf(graph.Channel);
		var scope = _panels[index].Scope;
		if (scope.IsMouseOver && scope.ActualWidth > 0)
		{
			var fraction = Math.Clamp(Mouse.GetPosition(scope).X / scope.ActualWidth, 0, 1);
			var now = _paused || Scrolled ? ViewTime() : MonotonicClock.Now;
			var time = now - _settings.DisplayDelaySeconds - ((1 - fraction) * _settings.HistorySeconds);
			var at = TimeProvider.System.GetLocalNow() - TimeSpan.FromSeconds(Math.Max(0, MonotonicClock.Now - time));
			var text = at.ToString("T", CultureInfo.CurrentCulture);
			menu.Items.Add(MenuEntry($"Mark {text}", null, () => Mark(at, text)));
			menu.Items.Add(MenuEntry("Copy this moment", null, () => CopyText(TooltipText(graph, fraction))));
			menu.Items.Add(new Separator());
		}

		menu.Items.Add(MenuEntry($"Hide {title}", Graph.KeyOf(index).ToString(), () => ToggleVisible(graph)));
		foreach (var (size, name) in new[] { (ChannelSize.Normal, "Normal size"), (ChannelSize.Tall, "Tall"), (ChannelSize.Collapsed, "Fold to its title") })
		{
			if (graph.Size != size)
			{
				menu.Items.Add(MenuEntry(name, $"Shift+{Graph.KeyOf(index)}", () => SetSize(graph, size)));
			}
		}

		if (graph.OrderName.Length > 0)
		{
			foreach (var (delta, name) in new[] { (-1, "Move up"), (1, "Move down") })
			{
				var order = Graph.DisplayOrder(_graphs, _settings.GraphOrder).Where(other => _graphs[other].OrderName.Length > 0).Select(other => _graphs[other].OrderName).ToList();
				var moved = LayoutOrder.Move(order, other => _graphs.First(candidate => candidate.OrderName == other).Visible, graph.OrderName, delta);
				if (!moved.SequenceEqual(order))
				{
					menu.Items.Add(MenuEntry(name, null, () =>
					{
						Change(settings => settings.GraphOrder = [.. moved]);
						BuildLayout();
					}));
				}
			}
		}

		if (_graphs.Count(other => other.Visible) > 1)
		{
			menu.Items.Add(MenuEntry($"Only {title}", null, () =>
			{
				foreach (var other in _graphs)
				{
					other.SetVisible(other == graph);
				}

				BuildLayout();
			}));
		}

		if (_graphs.Any(other => !other.Visible))
		{
			menu.Items.Add(MenuEntry("Show all graphs", null, () =>
			{
				foreach (var other in _graphs)
				{
					other.SetVisible(true);
				}

				BuildLayout();
			}));
		}

		menu.Items.Add(new Separator());
	}

	/// <summary>Puts a mark at <paramref name="at"/> on every running Pulse, this window included.</summary>
	private void Mark(DateTimeOffset at, string text)
	{
		try
		{
			MarkLog.Append(MarkLog.DefaultPath(), at, "mark", "Pulse", $"marked at {text}");
			ShowToast(Messages.Marked(text));
		}
		catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
		{
			ShowToast(Messages.SaveFailed(ex.Message));
		}
	}

	private void CopyText(string text)
	{
		try
		{
			Clipboard.SetText(text);
			ShowToast(Messages.Copied);
		}
		catch (System.Runtime.InteropServices.ExternalException)
		{
			ShowToast(Messages.ClipboardBusy);
		}
	}

	private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
	{
		var menu = Root.ContextMenu;
		menu.Items.Clear();
		AddTargetEntries(menu);
		menu.Items.Add(MenuEntry(_paused ? "Resume" : "Pause", "Space", TogglePause));
		menu.Items.Add(new Separator());

		var graphs = new MenuItem { Header = "Graphs" };
		for (var index = 0; index < _graphs.Count; index++)
		{
			var graph = _graphs[index];
			graphs.Items.Add(MenuEntry(
				ChannelText.TitleOf(graph.Channel),
				(index + 1).ToString(CultureInfo.InvariantCulture),
				() => ToggleVisible(graph),
				graph.Visible));
		}

		var history = new MenuItem { Header = "History" };
		foreach (var seconds in PulseSettings.HistoryPresets)
		{
			history.Items.Add(MenuEntry(FormatDuration(seconds), null, () => SetHistory(seconds), Math.Abs(_settings.HistorySeconds - seconds) < 1e-9));
		}

		var interval = new MenuItem { Header = "Sampling interval" };
		foreach (var seconds in PulseSettings.IntervalPresets)
		{
			interval.Items.Add(MenuEntry(FormatDuration(seconds), null, () => SetInterval(seconds), Math.Abs(_settings.IntervalSeconds - seconds) < 1e-9));
		}

		menu.Items.Add(graphs);
		menu.Items.Add(history);
		menu.Items.Add(interval);
		menu.Items.Add(new Separator());
		menu.Items.Add(MenuEntry("Per-core CPU", "K", ToggleCores, _settings.ShowCores));
		menu.Items.Add(MenuEntry("Min / max", "P", ToggleMinMax, _settings.ShowMinMax));
		menu.Items.Add(MenuEntry("Compact", "C", ToggleCompact, _settings.Compact));
		menu.Items.Add(MenuEntry("Markers", "M", ToggleMarkers, _settings.ShowMarkers));
		menu.Items.Add(MenuEntry("Notifications", "N", ToggleNotifications, _settings.Notify));
		menu.Items.Add(MenuEntry("Always on top", "T", ToggleTopmost, _settings.AlwaysOnTop));
		menu.Items.Add(MenuEntry("Low frame rate (30 fps)", "F", ToggleFrameRate, _settings.TargetFps < 60));
		menu.Items.Add(new Separator());
		menu.Items.Add(MenuEntry("Shortcuts", "F1", () => SetHelpVisible(true)));
		menu.Items.Add(MenuEntry("Quit", "Ctrl+Q", Close));
	}
}
