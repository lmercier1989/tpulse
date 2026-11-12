using System.Collections.Concurrent;
using System.IO;

using Pulse.Core.Collections;
using Pulse.Core.CommandLine;
using Pulse.Core.Diagnostics;
using Pulse.Core.Formatting;
using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Settings;
using Pulse.Core.Terminal;

namespace Pulse.Terminal;

/// <summary>
/// The monitor inside the terminal. One loop on the main thread reads keys, applies samples from the
/// sampling loop, and draws a frame when one is due; between frames it sleeps on a high-resolution timer
/// that also wakes on a key press. Graphs are drawn as Sixel images when the terminal supports them, as
/// braille characters otherwise. Only the parts of the screen that changed are sent to the terminal.
/// The arrow keys scroll back through the history while paused; a recording given with <c>-replay</c> is shown the
/// same way, starting at its end.
/// </summary>
internal sealed partial class TerminalApp
{
	private const double ReferenceCellHeight = 19;
	private const ushort LeftKey = 0x25;
	private const ushort RightKey = 0x27;
	private const ushort HomeKey = 0x24;
	private const ushort EndKey = 0x23;
	private const ushort EscapeKey = 0x1B;
	private const ushort EnterKey = 0x0D;
	private const ushort BackspaceKey = 0x08;
	private const ushort TabKey = 0x09;
	private const ushort UpKey = 0x26;
	private const ushort DownKey = 0x28;
	private const ushort MenuKey = 0x5D;
	private const ushort F10Key = 0x79;

	/// <summary>The page of the overview that is the layout screen (after the keys and the shell commands).</summary>
	private const int LayoutPage = 2;

	/// <summary>The flyout of a moment looked into: at most this wide, on a slightly lighter background than the graphs.</summary>
	private const int FlyoutWidth = 44;

	private static readonly Rgb FlyoutBackground = Rgb.FromHex(0x151922);

	/// <summary>A press of the mouse button at least this long is a look: letting go of the button lets go of the moment.</summary>
	private const double HoldSeconds = 0.4;

	/// <summary>While a recording plays, one window of history passes in this many seconds.</summary>
	private const double PlaySecondsPerWindow = 20;

	private readonly SystemMetricsCollector? _collector;
	private readonly Recording? _recording;
	private readonly SystemInfo _info;
	private readonly PulseSettings _saved;
	private readonly PulseSettings _settings;
	private readonly TerminalRenderer _renderer;
	private readonly List<Graph> _graphs;
	private IReadOnlyList<KeyGroup> _keys;
	private string _hints;
	private readonly ConcurrentQueue<MetricSnapshot> _samples = new();
	private readonly RingBuffer<MetricSnapshot> _history;
	private readonly EventHistory _events = new();
	private readonly AlertMonitor _alerts = new();
	private readonly IReadOnlyList<double> _historyPresets;
	private ScopeRaster[] _rasters;
	private readonly SixelEncoder _encoder = new();
	private readonly BrailleScope _braille = new();
	private readonly CurvePlanner _planner = new();
	private readonly AnsiWriter _frame = new();
	private readonly AnsiWriter _part = new();
	private byte[][] _lastPlot;
	private int[] _lastPlotLength;
	private byte[][] _lastPixels;
	private int[] _lastPixelCount;
	private string[] _lastHeader;
	private readonly RenderStatistics _statistics = new();
	private readonly List<int> _shown = [];
	private readonly List<MouseInput> _mouse = [];
	private (PanelLine Line, int LabelWidth)?[][] _lastPanel = [];
	private bool _panelDirty = true;
	private readonly string? _recordPath;
	private CsvRecorder? _recorder;
	private Notifier? _notifier;
	private double[] _primaryYs = [];
	private double[] _secondaryYs = [];
	private Sampler? _sampler;
	private TerminalLayout? _layout;
	private MetricSnapshot? _lastSample;
	private int _columns;
	private int _rows;
	private bool _sixel;
	private int _cellWidth = 10;
	private int _cellHeight = 20;
	private int _maxFps;
	private bool _layoutDirty = true;
	private bool _headersDirty = true;
	private bool _frameDirty = true;
	private bool _paused;
	private bool _playing;
	private bool _help;
	private bool _quit;
	private double _frozenTime;
	private double _viewEnd = double.NaN;
	private double _cursor = double.NaN;
	private double _cursorTime = double.NaN;
	private double _pinTime = double.NaN;
	private double _holdStart = double.NaN;
	private (int Column, int Row) _mouseCell = (-1, -1);
	private (int Column, int Row) _pinCell;
	private double _selectFrom = double.NaN;
	private double _selectTo = double.NaN;
	private double _dragFrom = double.NaN;
	private int _dragColumn;
	private bool _dragging;
	private Baseline? _baseline;
	private readonly Stack<(double History, double ViewEnd)> _zooms = new();
	private CpuStack? _stack;
	private int _helpPage;
	private (int Row, int Column, int Rows, int Width)? _flyout;
	private string _flyoutKey = string.Empty;
	private readonly List<(int Row, int Column, int Rows, int Width)> _erase = [];

	/// <summary>The part of each graph's plot (by graph index) that was left out for a flyout or menu, in cells; null for none.</summary>
	private readonly Dictionary<int, (int Row, int Column, int Rows, int Width)?> _lastCut = [];

	/// <summary>The flyout of this frame, placed before the graphs are drawn so they can leave its cells out.</summary>
	private ((int Row, int Column, int Rows, int Width) Area, IReadOnlyList<PanelLine> Lines, string Key)? _flyoutPlan;

	private byte[] _cropPixels = [];
	private List<MenuItem>? _menu;
	private int _menuSelected;
	private (int Row, int Column, int Rows, int Width)? _menuArea;
	private string _menuKey = string.Empty;
	private bool _leftHeld;
	private bool _rightHeld;
	private int _panColumn;
	private double _panEnd = double.NaN;
	private bool _panning;
	private int _layoutCursor;
	private List<int>? _soloRestore;
	private MetricSnapshot? _focusSample;
	private double _lastFrame;
	private double _nextFrame;
	private string? _toast;
	private double _toastUntil;
	private string _lastFooter = string.Empty;

	/// <summary>The footer as drawn last, to match a click against; see <see cref="KeyMap.FooterKeyAt"/>.</summary>
	private string _footerLine = string.Empty;

	/// <summary>The part of the footer under the mouse that a click works, lit like a button; null for none.</summary>
	private (int Start, int Length)? _footerHover;

	/// <summary>The row of the side panel under the mouse that a click opens a menu for, lit; null for none.</summary>
	private (int Part, int Row)? _panelHover;

	/// <param name="collector">The metric sources; null when a recording is played back.</param>
	/// <param name="recording">The recording of <c>-replay</c>; null when live.</param>
	/// <param name="saved">The remembered settings; changes made with the keys are applied here too.</param>
	/// <param name="settings">The settings of this launch.</param>
	/// <param name="options">The command line.</param>
	public TerminalApp(SystemMetricsCollector? collector, Recording? recording, PulseSettings saved, PulseSettings settings, LaunchOptions options)
	{
		_collector = collector;
		_recording = recording;
		_info = collector?.Info ?? recording?.Info ?? throw new ArgumentException("A collector or a recording is needed.", nameof(collector));
		_saved = saved;
		_settings = settings;
		_renderer = options.Renderer;
		_maxFps = options.Fps ?? 30;
		_recordPath = recording is null ? options.RecordPath : null;
		var capacity = recording is null ? Channel.Capacity : recording.Samples.Count + 32;
		_graphs = [.. Graph.CreateAll(settings, saved, _info, recording is null ? 0 : capacity)];
		_history = new RingBuffer<MetricSnapshot>(capacity);
		_historyPresets = recording is null ? PulseSettings.HistoryPresets : ReplayPresets(recording);

		var count = _graphs.Count;
		_rasters = [.. _graphs.Select(_ => new ScopeRaster(ScopePalette.Instance))];
		_lastPlot = [.. _graphs.Select(_ => Array.Empty<byte>())];
		_lastPlotLength = new int[count];
		_lastPixels = [.. _graphs.Select(_ => Array.Empty<byte>())];
		_lastPixelCount = new int[count];
		_lastHeader = new string[count];
		_hints = KeyMap.Hints(count, recording is not null);
		_keys = KeyMap.For(Surface.Terminal, [.. _graphs.Skip(MetricKinds.Builtin.Count).Select(graph => ChannelText.TitleOf(graph.Channel))], recording is not null);
	}

	/// <summary>The CSV file samples were written to, when recording.</summary>
	public string? RecordedFile { get; private set; }

	public void Run(ConsoleHost host)
	{
		if (_recordPath is not null)
		{
			_recorder = new CsvRecorder(_recordPath.Length > 0 ? _recordPath : CsvRecorder.DefaultPath(_info.Host), _info.Watches);
			RecordedFile = _recorder.FilePath;
			Log.Info($"Recording samples to {RecordedFile}.");
		}

		if (_settings.Notify && _recording is null)
		{
			_notifier = new Notifier();
		}

		host.Enter();
		try
		{
			(_columns, _rows) = host.Size;
			DetectRenderer(host);
			if (_recording is not null)
			{
				LoadRecording(_recording);
				Loop(host);
				return;
			}

			using var sampler = new Sampler(_collector!, TimeSpan.FromSeconds(_settings.IntervalSeconds), _samples.Enqueue);
			_sampler = sampler;
			sampler.Start();
			Loop(host);
		}
		finally
		{
			host.Exit();
			_recorder?.Dispose();
			_notifier?.Dispose();
		}
	}

	/// <summary>
	/// The history presets of a recording: the usual ones, then 15 minutes, an hour and the whole recording, as far as
	/// the recording is long.
	/// </summary>
	private static List<double> ReplayPresets(Recording recording)
	{
		var length = Math.Max(10, recording.Samples[^1].Time - recording.Samples[0].Time);
		var presets = new List<double>();
		foreach (var preset in new double[] { 30, 60, 300, 900, 3600 })
		{
			if (preset < length)
			{
				presets.Add(preset);
			}
		}

		presets.Add(Math.Ceiling(length));
		return presets;
	}

	private void Loop(ConsoleHost host)
	{
		var keys = new List<KeyPress>();
		_lastFrame = MonotonicClock.Now;
		while (!_quit)
		{
			var (columns, rows) = host.Size;
			if (columns != _columns || rows != _rows)
			{
				(_columns, _rows) = (columns, rows);
				_layoutDirty = true;
				if (_sixel)
				{
					RefreshCellSize(host);
				}
			}

			keys.Clear();
			_mouse.Clear();
			host.ReadInput(keys, _mouse);
			foreach (var key in keys)
			{
				Handle(key);
			}

			foreach (var mouse in _mouse)
			{
				HandleMouse(mouse);
			}

			if (_quit)
			{
				break;
			}

			DrainSamples();
			if (_layoutDirty)
			{
				Relayout(host);
			}

			if (_panelDirty && !_help)
			{
				DrawPanel(host);
			}

			if (_help)
			{
				host.Wait(0.25);
				continue;
			}

			if ((_paused || Scrolled) && !_playing)
			{
				// The picture stays at the moment of the pause or of the scroll (sampling goes on while scrolled); only a
				// change redraws it.
				if (_frameDirty || _headersDirty)
				{
					DrawFrame(host, ViewTime(), 0);
					_frameDirty = false;
				}
				else
				{
					DrawFooter(host);
				}

				host.Wait(0.25);
				continue;
			}

			var now = MonotonicClock.Now;
			if (now >= _nextFrame)
			{
				var delta = Math.Min(now - _lastFrame, 0.25);
				if (_playing)
				{
					Play(delta);
				}

				DrawFrame(host, _paused || Scrolled ? ViewTime() : now, delta);
				_lastFrame = now;
				_nextFrame = now + (1 / FramesPerSecond());
			}
			else
			{
				DrawFooter(host);
			}

			host.Wait(_nextFrame - MonotonicClock.Now);
		}
	}

	/// <summary>
	/// Frames are only needed as fast as the trace moves: about one per pixel of scroll, between 4 per second
	/// and the cap. Five minutes of history therefore costs far less than thirty seconds.
	/// </summary>
	private double FramesPerSecond()
	{
		var width = _layout?.Width ?? 80;
		var pixelsPerSecond = (_sixel ? width * _cellWidth : width * 2) / _settings.HistorySeconds;
		if (_playing)
		{
			pixelsPerSecond *= _settings.HistorySeconds / PlaySecondsPerWindow;
		}

		return Math.Clamp(pixelsPerSecond, 4, _maxFps);
	}

	// ---- Terminal capabilities -------------------------------------------------------------------------

	private void DetectRenderer(ConsoleHost host)
	{
		if (_renderer == TerminalRenderer.Braille)
		{
			_sixel = false;
			Log.Info("Terminal renderer: braille (requested).");
			return;
		}

		var replies = TerminalReplies.Parse(host.Query(TerminalReplies.Query, TerminalReplies.IsComplete, 1.0));
		var cell = replies.ResolveCellSize(_columns, _rows);
		_sixel = _renderer == TerminalRenderer.Sixel || (replies.SupportsSixel == true && cell is not null);
		if (cell is { } size)
		{
			(_cellWidth, _cellHeight) = size;
		}

		Log.Info($"Terminal: {_columns}x{_rows} cells, sixel {replies.SupportsSixel?.ToString() ?? "unknown"}, "
				 + $"cell {replies.CellWidth}x{replies.CellHeight} px, area {replies.AreaWidth}x{replies.AreaHeight} px; "
				 + $"renderer {(_sixel ? $"sixel at {_cellWidth}x{_cellHeight} px per cell" : "braille")}.");
	}

	private void RefreshCellSize(ConsoleHost host)
	{
		var replies = TerminalReplies.Parse(host.Query(TerminalReplies.Query, TerminalReplies.IsComplete, 0.5));
		if (replies.ResolveCellSize(_columns, _rows) is { } size)
		{
			(_cellWidth, _cellHeight) = size;
		}
	}

	// ---- Samples ---------------------------------------------------------------------------------------

	/// <summary>A recording: every row at once, then paused at its end so it can be scrolled through.</summary>
	private void LoadRecording(Recording recording)
	{
		foreach (var sample in recording.Samples)
		{
			Apply(sample);
		}

		_settings.HistorySeconds = Math.Min(_settings.HistorySeconds, _historyPresets[^1]);
		_paused = true;
		_frozenTime = recording.Samples[^1].Time + _settings.DisplayDelaySeconds;
		RefreshChannels();
		Log.Info($"Replaying {recording.FilePath}: {recording.Samples.Count} samples from {recording.Start:O} to {recording.End:O}.");
	}

	private void DrainSamples()
	{
		while (_samples.TryDequeue(out var sample))
		{
			if (_paused)
			{
				continue;
			}

			Apply(sample);
			_recorder?.Write(sample);
			if (_notifier is not null)
			{
				foreach (var alert in _alerts.Check(_graphs.Select(graph => graph.Channel), sample, TimeProvider.System.GetUtcNow()))
				{
					_notifier.Show(alert.Title, alert.Text);
				}
			}

			RefreshChannels();
			_stack = null;
			_panelDirty = true;
		}
	}

	private void Apply(MetricSnapshot sample)
	{
		_lastSample = sample;
		_history.Add(sample);
		_events.Add(sample.Events);
		foreach (var graph in _graphs)
		{
			graph.Channel.Append(sample);
		}
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

		_headersDirty = true;
	}

	/// <summary>Whether the view is held at an earlier moment by scrolling; sampling goes on meanwhile.</summary>
	private bool Scrolled => !double.IsNaN(_viewEnd);

	/// <summary>The newest moment the view can show: now, or the moment of the pause (the end of a recording).</summary>
	private double EdgeTime() => _paused ? _frozenTime : MonotonicClock.Now;

	/// <summary>The clock of the right edge plus the display delay: the scrolled-to moment, or else the edge.</summary>
	private double ViewTime() => Scrolled ? _viewEnd : EdgeTime();

	/// <summary>The oldest moment the view can end at: the oldest sample at the left edge.</summary>
	private double OldestViewTime()
		=> _history.Count == 0 ? EdgeTime() : _history[0].Time + _settings.DisplayDelaySeconds + _settings.HistorySeconds;

	/// <summary>The sample at the right edge of the view, for the values in the headers and the panel.</summary>
	private MetricSnapshot? ViewSample()
		=> !Scrolled || _history.Count == 0 ? _lastSample : SampleAt(ViewTime() - _settings.DisplayDelaySeconds);

	/// <summary>The newest sample at or before <paramref name="time"/>, for the values under the mouse cursor.</summary>
	private MetricSnapshot? SampleAt(double time)
	{
		for (var i = _history.Count - 1; i >= 0; i--)
		{
			if (_history[i].Time <= time)
			{
				return _history[i];
			}
		}

		return _history.Count > 0 ? _history[0] : _lastSample;
	}

	/// <summary>
	/// The mouse: the wheel scrolls through the history, a click on a header folds the graph or opens it again, over a
	/// plot a cursor shows the values of that moment in every header and what was busiest then. Pressing the left button
	/// on a plot freezes that moment (held for a while, letting go lets go of it too; a short click keeps it until the
	/// next click or Esc), dragging selects a range. The right button opens the menu of what is under the mouse, or pans
	/// through time when it drags. While the menu is open, the mouse works the menu.
	/// </summary>
	private void HandleMouse(MouseInput mouse)
	{
		if (_layout is null)
		{
			return;
		}

		if (_help)
		{
			HandleHelpMouse(mouse);
			return;
		}

		var leftDown = mouse.LeftButton && !_leftHeld;
		var leftUp = !mouse.LeftButton && _leftHeld;
		var rightDown = mouse.RightButton && !_rightHeld;
		var rightUp = !mouse.RightButton && _rightHeld;
		_leftHeld = mouse.LeftButton;
		_rightHeld = mouse.RightButton;
		_mouseCell = (mouse.Column, mouse.Row);
		if (_menu is not null)
		{
			HandleMenuMouse(mouse, leftDown || rightDown);
			return;
		}

		if (mouse.Wheel != 0)
		{
			Scroll(mouse.Wheel * _settings.HistorySeconds / 8);
			return;
		}

		UpdateHover(mouse.Column, mouse.Row);
		var hit = _layout.HitTest(mouse.Column, mouse.Row);
		var cursor = hit.Part == LayoutPart.Plot ? hit.Fraction : double.NaN;
		if (!(double.IsNaN(cursor) && double.IsNaN(_cursor)) && cursor != _cursor)
		{
			_cursor = cursor;
			_cursorTime = double.NaN;
			_headersDirty = true;
			RedrawWhilePaused();
		}

		// The right button: a click opens the menu of what is under the mouse, a drag pans through time.
		if (rightDown)
		{
			_panColumn = mouse.Column;
			_panEnd = ViewTime();
			_panning = false;
			return;
		}

		if (mouse.RightButton && mouse.Moved)
		{
			var columns = mouse.Column - _panColumn;
			if (_panning || Math.Abs(columns) >= 1)
			{
				_panning = true;
				var target = _panEnd - (columns * _settings.HistorySeconds / Math.Max(1, _layout.Width));
				Scroll(ViewTime() - target);
			}

			return;
		}

		if (rightUp)
		{
			if (!_panning)
			{
				OpenMenu(mouse.Column, mouse.Row);
			}

			_panning = false;
			return;
		}

		if (mouse.Moved)
		{
			// Dragging across a plot with the button down selects a range of time; a press without moving looks at a moment.
			if (mouse.LeftButton && !double.IsNaN(_dragFrom) && hit.Part == LayoutPart.Plot && (_dragging || Math.Abs(mouse.Column - _dragColumn) >= 2))
			{
				_dragging = true;
				_pinTime = double.NaN;
				var time = TimeAtFraction(hit.Fraction);
				(_selectFrom, _selectTo) = time < _dragFrom ? (time, _dragFrom) : (_dragFrom, time);
				_panelDirty = true;
				_headersDirty = true;
				RedrawWhilePaused();
			}

			return;
		}

		if (leftUp)
		{
			_dragFrom = double.NaN;
			if (_dragging)
			{
				_dragging = false;
				_holdStart = double.NaN;
				return;
			}

			// The button went up: a press held for a while was a look, so the moment lets go; a short click keeps it.
			if (!double.IsNaN(_holdStart) && MonotonicClock.Now - _holdStart >= HoldSeconds)
			{
				LetGo();
			}

			_holdStart = double.NaN;
			return;
		}

		// Off the graphs a click works what is under it: a row of the side panel opens its menu, a hint in the footer
		// presses its key.
		if (leftDown && hit.Slot < 0)
		{
			if (PanelHit(mouse.Column, mouse.Row) is { } row && IsClickable(row.Line))
			{
				OpenMenu(mouse.Column, mouse.Row);
			}
			else if (mouse.Row == _layout.FooterRow && KeyMap.FooterKeyAt(_footerLine, mouse.Column - _layout.Left) is { } hint)
			{
				PressFooterKey(hint.Key);
			}

			return;
		}

		if (!leftDown || hit.Slot < 0)
		{
			return;
		}

		var graph = _graphs[_shown[_layout.Slots[hit.Slot].Index]];
		if (hit.Part == LayoutPart.Header)
		{
			graph.SetSize(graph.Size == ChannelSize.Collapsed ? ChannelSize.Normal : ChannelSize.Collapsed);
			_layoutDirty = true;
			return;
		}

		if (hit.Part != LayoutPart.Plot)
		{
			return;
		}

		_dragFrom = TimeAtFraction(hit.Fraction);
		_dragColumn = mouse.Column;
		if (!double.IsNaN(_pinTime))
		{
			LetGo();
			return;
		}

		_pinTime = _dragFrom;
		_pinCell = (mouse.Column, mouse.Row);
		_holdStart = MonotonicClock.Now;
		_panelDirty = true;
		_headersDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>
	/// The mouse over the overview and the layout screen: on the layout screen a click on a graph or a section shows or
	/// hides it, like Space; on the overview a click goes back, like any key.
	/// </summary>
	private void HandleHelpMouse(MouseInput mouse)
	{
		var leftDown = mouse.LeftButton && !_leftHeld;
		_leftHeld = mouse.LeftButton;
		_rightHeld = mouse.RightButton;
		if (!leftDown)
		{
			return;
		}

		if (_helpPage != LayoutPage)
		{
			_help = false;
			_layoutDirty = true;
			return;
		}

		if (LayoutEntryAt(mouse.Column, mouse.Row) is var index and >= 0)
		{
			_layoutCursor = index;
			HandleLayoutKey(new KeyPress(' ', ' ', Shift: false, Control: false, Alt: false));
		}
	}

	/// <summary>
	/// Lights what a click works under the mouse: a row of the side panel that has a menu, a hint of the footer. Only the
	/// rows and the footer that change are written again.
	/// </summary>
	private void UpdateHover(int column, int row)
	{
		var layout = _layout!;
		(int Part, int Row)? panel = PanelHit(column, row) is { } found && IsClickable(found.Line) ? (found.Part, found.Row) : null;
		if (panel != _panelHover)
		{
			foreach (var cell in new[] { _panelHover, panel })
			{
				if (cell is { } at && at.Part < _lastPanel.Length && at.Row < _lastPanel[at.Part].Length)
				{
					_lastPanel[at.Part][at.Row] = null;
				}
			}

			_panelHover = panel;
			_panelDirty = true;
		}

		(int Start, int Length)? footer = row == layout.FooterRow && KeyMap.FooterKeyAt(_footerLine, column - layout.Left) is { } hint ? (hint.Start, hint.Length) : null;
		if (footer != _footerHover)
		{
			_footerHover = footer;
			_lastFooter = string.Empty;
			RedrawWhilePaused();
		}
	}

	/// <summary>A click on a hint of the footer: the key it names, as if it was pressed.</summary>
	private void PressFooterKey(string key)
	{
		if (key == "Right-click")
		{
			OpenMenuAtCursor();
			return;
		}

		var press = key switch
		{
			"Space" => new KeyPress(' ', ' ', false, false, false),
			"Esc" => new KeyPress(EscapeKey, '\u001b', false, false, false),
			"Enter" => new KeyPress(EnterKey, '\r', false, false, false),
			"Backspace" => new KeyPress(BackspaceKey, '\b', false, false, false),
			"End" => new KeyPress(EndKey, '\0', false, false, false),
			"Home" => new KeyPress(HomeKey, '\0', false, false, false),
			"?" => new KeyPress(0, '?', false, false, false),
			_ => new KeyPress(key[0], char.ToLowerInvariant(key[0]), false, false, false),
		};
		Handle(press);
	}

	/// <summary>The moment at <paramref name="fraction"/> of the plot width, on the sample clock.</summary>
	private double TimeAtFraction(double fraction)
		=> (_paused || Scrolled ? ViewTime() : MonotonicClock.Now) - _settings.DisplayDelaySeconds - ((1 - fraction) * _settings.HistorySeconds);

	/// <summary>Lets go of a frozen moment: the headers and the panel follow the mouse, or the live values, again.</summary>
	private void LetGo()
	{
		_pinTime = double.NaN;
		_holdStart = double.NaN;
		_panelDirty = true;
		_headersDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>"Frozen 14:32:05 · Esc let go": the moment held with the mouse button.</summary>
	private string FrozenText()
	{
		var at = _recording is not null ? SampleAt(_pinTime)?.RecordedAt?.LocalDateTime : WallTimeOf(_pinTime).LocalDateTime;
		return Messages.Frozen(at?.ToString("T", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty);
	}

	/// <summary>The wall-clock time of a moment on the sample clock.</summary>
	private static DateTimeOffset WallTimeOf(double time)
		=> TimeProvider.System.GetLocalNow() - TimeSpan.FromSeconds(Math.Max(0, MonotonicClock.Now - time));

	/// <summary>
	/// What the panel looks into for the moment of <paramref name="sample"/>: its time, whether it is frozen, the events
	/// within 10 seconds of it, and the processes that carried the load over the visible window.
	/// </summary>
	private PanelFocus FocusOn(MetricSnapshot sample)
	{
		var at = sample.RecordedAt ?? WallTimeOf(sample.Time);
		var events = _events.Between(sample.Time - 10, sample.Time + 10).ToList();
		var right = (_paused || Scrolled ? ViewTime() : MonotonicClock.Now) - _settings.DisplayDelaySeconds;
		var left = right - _settings.HistorySeconds;
		var inView = new List<MetricSnapshot>();
		for (var i = _history.Count - 1; i >= 0 && _history[i].Time >= left; i--)
		{
			if (_history[i].Time <= right)
			{
				inView.Add(_history[i]);
			}
		}

		return new PanelFocus(at, !double.IsNaN(_pinTime), events, ProcessCausation.AverageCpu(inView, 5));
	}

	// ---- Drawing ---------------------------------------------------------------------------------------

	private void Relayout(ConsoleHost host)
	{
		_shown.Clear();
		foreach (var index in Graph.DisplayOrder(_graphs, _settings.GraphOrder))
		{
			if (_graphs[index].Visible)
			{
				_shown.Add(index);
			}
		}

		var graphs = _shown.Select(index => (_graphs[index].Kind, _graphs[index].Size)).ToList();
		var panelWidth = _settings.ShowPanel ? TerminalLayout.PanelWidthFor(_columns, _settings.Compact) : 0;
		_layout = TerminalLayout.Compute(_columns, _rows, graphs, _settings.Compact, panelWidth);
		_lastPanel = [new (PanelLine, int)?[_layout.PanelRows], new (PanelLine, int)?[_layout.PanelRows]];
		_panelDirty = true;
		_layoutDirty = false;
		_headersDirty = true;
		_lastFooter = string.Empty;
		_flyout = null;
		_flyoutKey = string.Empty;
		_erase.Clear();
		Array.Clear(_lastHeader);
		Array.Clear(_lastPlotLength);
		Array.Clear(_lastPixelCount);

		_frame.Clear();
		_frame.Background(ScopeColors.Background).Ascii("\u001b[2J");
		if (_help && _helpPage == LayoutPage)
		{
			WriteLayout();
		}
		else if (_help)
		{
			WriteHelp();
		}
		else if (_layout.Slots.Count == 0)
		{
			var hint = $"All graphs are hidden. Press {Graph.KeyRange(1, _graphs.Count)} to show one.";
			_frame.MoveTo(_rows / 2, Math.Max(0, (_columns - hint.Length) / 2)).Foreground(ScopeColors.TextSecondary).Text(hint);
		}

		host.Write(_frame.Written);
		_nextFrame = 0;
		_frameDirty = true;
	}

	private void DrawFrame(ConsoleHost host, double now, double deltaSeconds)
	{
		var started = MonotonicClock.Now;
		var layout = _layout!;

		// The cursor stays at its place on screen, so live its moment moves on with the trace: the headers follow it. A
		// moment frozen with the mouse button wins over the cursor, wherever the mouse goes; the panel looks into it.
		_cursorTime = !double.IsNaN(_pinTime) ? _pinTime
			: double.IsNaN(_cursor) ? double.NaN
			: now - _settings.DisplayDelaySeconds - ((1 - _cursor) * _settings.HistorySeconds);
		var focus = double.IsNaN(_cursorTime) ? null : SampleAt(_cursorTime);
		if (!double.IsNaN(_cursorTime))
		{
			_headersDirty = true;
		}

		if (!ReferenceEquals(focus, _focusSample))
		{
			_focusSample = focus;
			_panelDirty |= _settings.FocusInPanel;
		}
		PlaceFlyout(layout);
		_frame.Clear();
		_frame.Ascii("\u001b[?2026h");
		var changed = _erase.Count > 0;
		foreach (var area in _erase)
		{
			for (var row = area.Row; row < area.Row + area.Rows; row++)
			{
				// Blanked, not erased: erasing would delete the whole graph image underneath in Konsole; the graphs under
				// the area are sent again in this frame and cover these cells.
				_frame.MoveTo(row, area.Column).Background(ScopeColors.Background).Blank(area.Width);
			}

			if (layout.PanelWidth > 0 && area.Column + area.Width > layout.PanelLeft)
			{
				PanelChanged();
			}

			if (area.Row + area.Rows > layout.FooterRow)
			{
				_lastFooter = string.Empty;
			}
		}

		_erase.Clear();

		foreach (var slot in layout.Slots)
		{
			var index = _shown[slot.Index];
			var channel = _graphs[index].Channel;
			channel.Scale.Advance(deltaSeconds);
			if (_headersDirty)
			{
				changed |= DrawHeader(slot, layout, channel, index);
			}

			if (slot.PlotRows > 0)
			{
				_part.Clear();
				if (_sixel)
				{
					DrawSixelPlot(slot, layout, channel, index, now);
				}
				else
				{
					DrawBraillePlot(slot, layout, channel, index, now);
				}

				changed |= AppendIfChanged(index);
			}
		}

		_headersDirty = false;
		changed |= DrawFlyout(changed);
		changed |= DrawMenu(layout, changed);
		changed |= AppendFooter();
		_frame.Ascii("\u001b[?2026l");
		if (changed)
		{
			host.Write(_frame.Written);
		}

		_statistics.Add(MonotonicClock.Now - started, changed ? _frame.Length : 0, _sixel);
	}

	private bool DrawHeader(GraphSlot slot, TerminalLayout layout, Channel channel, int index)
	{
		var showStats = _settings.ShowMinMax && !_settings.Compact;
		var readout = ChannelText.Describe(channel, _info, double.IsNaN(_cursorTime) ? ViewSample() : SampleAt(_cursorTime), showStats);
		var key = readout.ToString();
		if (key == _lastHeader[index])
		{
			return false;
		}

		_lastHeader[index] = key;
		HeaderLine.Write(_frame, slot.HeaderRow, layout.Left, layout.Width, slot.Kind, readout, _settings.Compact);
		return true;
	}

	private void DrawSixelPlot(GraphSlot slot, TerminalLayout layout, Channel channel, int index, double now)
	{
		var width = layout.Width * _cellWidth;
		var height = slot.PlotRows * _cellHeight / 6 * 6;
		if (width < 8 || height < 6)
		{
			return;
		}

		var kind = slot.Kind;
		var scale = Math.Clamp(_cellHeight / ReferenceCellHeight, 0.75, 3);
		var thickness = 1.6 * scale;
		var dotRadius = 2.2 * scale;
		var top = Math.Ceiling(thickness) + 1;
		var bottom = height - 1 - (thickness / 2);
		var right = width - Math.Ceiling(dotRadius) - 2;
		var map = new PlotMapping(now - _settings.DisplayDelaySeconds, right, right / _settings.HistorySeconds, top, bottom, (bottom - top) / channel.Scale.Current);

		var raster = _rasters[index];
		raster.Reset(width, height);
		raster.DrawGrid(top, bottom, right);
		if (HasSelection)
		{
			raster.DrawBand(map.X(_selectFrom), map.X(_selectTo), top, bottom, ScopePalette.SelectionBand);
			raster.DrawCursor(map.X(_selectFrom), top, bottom, ScopePalette.SelectionEdge);
			raster.DrawCursor(map.X(_selectTo), top, bottom, ScopePalette.SelectionEdge);
		}

		if (_settings.ShowMarkers)
		{
			var title = ChannelText.TitleOf(channel);
			foreach (var item in _events.Between(map.TimeAt(0), map.RightTime))
			{
				if (item.BelongsTo(title))
				{
					raster.DrawMarker(map.X(item.Time), top, bottom, ScopePalette.Marker(item.Kind));
				}
			}
		}

		if (!double.IsNaN(_cursorTime))
		{
			raster.DrawCursor(map.X(_cursorTime), top, bottom, ScopePalette.Cursor);
		}

		var columns = width + 1;
		EnsureColumns(columns);

		// Stacked, the CPU graph shows its busiest processes as bands under the total, without the other traces.
		var stacked = kind == MetricKind.Cpu && _settings.StackCpu;
		if (stacked)
		{
			DrawStack(raster, map, (int) Math.Ceiling(right), bottom);
		}
		else
		{
			if (_settings.ShowCores && channel.Cores.Length > 0)
			{
				foreach (var core in channel.Cores)
				{
					_planner.Plan(core, map, _settings.GapSeconds);
					_planner.SampleColumns(_secondaryYs.AsSpan(0, columns));
					raster.DrawLine(_secondaryYs.AsSpan(0, columns), 0.9 * scale, ScopePalette.Core(kind, 1), ScopePalette.CoreLevels);
				}
			}

			if (channel.Secondary is not null)
			{
				_planner.Plan(channel.Secondary, map, _settings.GapSeconds);
				_planner.SampleColumns(_secondaryYs.AsSpan(0, columns));
				raster.DrawLine(_secondaryYs.AsSpan(0, columns), 1.25 * scale, ScopePalette.SecondaryStroke(kind, 1), ScopePalette.StrokeLevels);
				DrawHead(raster, right, dotRadius * 0.85, ScopePalette.SecondaryStroke(kind, 1));
			}
		}

		if (PlanGhost(index, map, columns))
		{
			raster.DrawLine(_secondaryYs.AsSpan(0, columns), 1.1 * scale, ScopePalette.Ghost(1), ScopePalette.GhostLevels);
		}

		_planner.Plan(channel.Primary, map, _settings.GapSeconds);
		_planner.SampleColumns(_primaryYs.AsSpan(0, columns));
		if (!stacked)
		{
			raster.DrawFill(_primaryYs.AsSpan(0, columns), top, bottom, ScopePalette.Fill(kind, 1), ScopePalette.FillLevels, ScopePalette.FillAlpha);
		}

		raster.DrawLine(_primaryYs.AsSpan(0, columns), thickness, ScopePalette.Stroke(kind, 1), ScopePalette.StrokeLevels);
		DrawHead(raster, right, dotRadius, ScopePalette.Stroke(kind, 1));

		// A trace that did not move by a whole pixel (a flat line, a long history) needs no new image, unless a flyout or
		// menu came, went or moved over it.
		var pixels = raster.Pixels;
		var cut = CutFor(slot, layout);
		if (pixels.SequenceEqual(_lastPixels[index].AsSpan(0, _lastPixelCount[index])) && _lastCut.GetValueOrDefault(index) == cut)
		{
			return;
		}

		_lastCut[index] = cut;
		if (_lastPixels[index].Length < pixels.Length)
		{
			_lastPixels[index] = new byte[pixels.Length];
		}

		pixels.CopyTo(_lastPixels[index]);
		_lastPixelCount[index] = pixels.Length;

		// The image is opaque, background included, so it covers the previous one completely and nothing is erased
		// first. Some terminals (Konsole) draw images outside synchronized updates and would show an erase as a grey
		// flash between the two images.
		if (cut is not { } hole)
		{
			_part.MoveTo(slot.PlotRow, layout.Left);
			_encoder.Encode(pixels, width, height, ScopePalette.Instance, _part, opaque: true);
			return;
		}

		// Around a flyout or menu: above it, left and right of it, and below it, so no text of the box falls into the first
		// row of an image (Konsole deletes the image then) and no image covers the box.
		var plotBottom = slot.PlotRow + slot.PlotRows;
		var plotRight = layout.Left + layout.Width;
		WriteCrop(pixels, width, height, slot.PlotRow, layout.Left, slot.PlotRow, layout.Left, hole.Row, plotRight);
		WriteCrop(pixels, width, height, slot.PlotRow, layout.Left, hole.Row, layout.Left, plotBottom, hole.Column);
		WriteCrop(pixels, width, height, slot.PlotRow, layout.Left, hole.Row, hole.Column + hole.Width, plotBottom, plotRight);
		WriteCrop(pixels, width, height, slot.PlotRow, layout.Left, hole.Row + hole.Rows, hole.Column, plotBottom, hole.Column + hole.Width);
	}

	/// <summary>
	/// Sends the cells from (<paramref name="fromRow"/>, <paramref name="fromColumn"/>) up to (<paramref name="toRow"/>,
	/// <paramref name="toColumn"/>) of a plot image whose top left cell is (<paramref name="plotRow"/>, <paramref name="plotColumn"/>)
	/// as an image of their own; nothing when that is empty.
	/// </summary>
	private void WriteCrop(ReadOnlySpan<byte> pixels, int width, int height, int plotRow, int plotColumn, int fromRow, int fromColumn, int toRow, int toColumn)
	{
		var x = (fromColumn - plotColumn) * _cellWidth;
		var y = (fromRow - plotRow) * _cellHeight;
		var cropWidth = (toColumn - fromColumn) * _cellWidth;
		var cropHeight = Math.Min(height, (toRow - plotRow) * _cellHeight) - y;
		if (cropWidth <= 0 || cropHeight <= 0)
		{
			return;
		}

		if (_cropPixels.Length < cropWidth * cropHeight)
		{
			_cropPixels = new byte[cropWidth * cropHeight];
		}

		for (var line = 0; line < cropHeight; line++)
		{
			pixels.Slice(((y + line) * width) + x, cropWidth).CopyTo(_cropPixels.AsSpan(line * cropWidth));
		}

		_part.MoveTo(fromRow, fromColumn);
		_encoder.Encode(_cropPixels.AsSpan(0, cropWidth * cropHeight), cropWidth, cropHeight, ScopePalette.Instance, _part, opaque: true);
	}

	/// <summary>The cells of the plot of <paramref name="slot"/> under the open menu or the flyout, or null when none is over it.</summary>
	private (int Row, int Column, int Rows, int Width)? CutFor(GraphSlot slot, TerminalLayout layout)
	{
		if ((_menu is not null ? _menuArea : _flyoutPlan?.Area) is not { } box)
		{
			return null;
		}

		var top = Math.Max(box.Row, slot.PlotRow);
		var bottom = Math.Min(box.Row + box.Rows, slot.PlotRow + slot.PlotRows);
		var left = Math.Max(box.Column, layout.Left);
		var right = Math.Min(box.Column + box.Width, layout.Left + layout.Width);
		return top < bottom && left < right ? (top, left, bottom - top, right - left) : null;
	}

	/// <summary>
	/// Plans the baseline of graph <paramref name="index"/> into the secondary columns, moved to where it is compared
	/// (<see cref="GhostShift"/>); false when there is no baseline for it.
	/// </summary>
	private bool PlanGhost(int index, PlotMapping map, int columns)
	{
		if (_baseline is null || index >= _baseline.Traces.Length || _baseline.Traces[index] is not { } ghost || ghost.Samples.Count < 2)
		{
			return false;
		}

		_planner.Plan(ghost, map with { RightTime = map.RightTime - GhostShift(map.RightTime) }, _settings.GapSeconds);
		_planner.SampleColumns(_secondaryYs.AsSpan(0, columns));
		return true;
	}

	/// <summary>The stacked CPU graph: per pixel column a band per busiest process from the bottom up, then the rest up to the total.</summary>
	private void DrawStack(ScopeRaster raster, PlotMapping map, int width, double bottom)
	{
		var stack = StackOf();
		Span<double> tops = stackalloc double[CpuStack.Named + 1];
		for (var x = 0; x < width; x++)
		{
			if (!stack.TryTops(_history, map.TimeAt(x + 0.5), _settings.GapSeconds, tops))
			{
				continue;
			}

			var lower = bottom;
			for (var band = 0; band <= CpuStack.Named; band++)
			{
				var upper = map.Y(tops[band]);
				raster.DrawStackColumn(x, upper, lower, ScopePalette.StackFill(band), ScopePalette.StackEdge(band));
				lower = upper;
			}
		}
	}

	/// <summary>The stacked CPU graph in braille: every cell takes the colour of the band at its centre.</summary>
	private void DrawBrailleStack(PlotMapping map)
	{
		var stack = StackOf();
		Span<double> tops = stackalloc double[CpuStack.Named + 1];
		for (var column = 0; column < _braille.Columns; column++)
		{
			if (!stack.TryTops(_history, map.TimeAt((column * 2) + 1), _settings.GapSeconds, tops))
			{
				continue;
			}

			for (var row = 0; row < _braille.Rows; row++)
			{
				var value = (map.Bottom - ((row * 4) + 2.0)) / map.PixelsPerUnit;
				for (var band = 0; band <= CpuStack.Named; band++)
				{
					if (value <= tops[band])
					{
						_braille.SetStack(column, row, band);
						break;
					}
				}
			}
		}
	}

	/// <summary>The beam dot where the last planned trace meets the right edge (or ends before it).</summary>
	private void DrawHead(ScopeRaster raster, double right, double radius, byte firstRegister)
	{
		if (!_planner.TryGetEnd(out var x, out var y))
		{
			return;
		}

		if (x > right)
		{
			x = right;
			if (!_planner.TryGetY(right, out y))
			{
				return;
			}
		}

		if (x >= 0)
		{
			raster.DrawDot(x, y, radius, firstRegister, ScopePalette.StrokeLevels);
		}
	}

	private void DrawBraillePlot(GraphSlot slot, TerminalLayout layout, Channel channel, int index, double now)
	{
		_braille.Reset(layout.Width, slot.PlotRows);
		var right = _braille.DotWidth - 1.0;
		var bottom = _braille.DotHeight - 1.0;
		var map = new PlotMapping(now - _settings.DisplayDelaySeconds, right, right / _settings.HistorySeconds, 0, bottom, bottom / channel.Scale.Current);
		var columns = _braille.DotWidth + 1;
		EnsureColumns(columns);
		_braille.DrawGrid();
		if (HasSelection)
		{
			_braille.DrawBand(map.X(_selectFrom), map.X(_selectTo));
		}

		if (_settings.ShowMarkers)
		{
			var title = ChannelText.TitleOf(channel);
			foreach (var item in _events.Between(map.TimeAt(0), map.RightTime))
			{
				if (item.BelongsTo(title))
				{
					_braille.DrawMarker(map.X(item.Time), item.Kind);
				}
			}
		}

		if (!double.IsNaN(_cursorTime))
		{
			_braille.DrawMarker(map.X(_cursorTime), EventKind.Git);
		}

		var stacked = channel.Kind == MetricKind.Cpu && _settings.StackCpu;
		if (stacked)
		{
			DrawBrailleStack(map);
		}
		else
		{
			if (_settings.ShowCores && channel.Cores.Length > 0)
			{
				foreach (var core in channel.Cores)
				{
					_planner.Plan(core, map, _settings.GapSeconds);
					_planner.SampleColumns(_secondaryYs.AsSpan(0, columns));
					_braille.DrawLine(_secondaryYs.AsSpan(0, columns), BrailleLayer.Core);
				}
			}

			if (channel.Secondary is not null)
			{
				_planner.Plan(channel.Secondary, map, _settings.GapSeconds);
				_planner.SampleColumns(_secondaryYs.AsSpan(0, columns));
				_braille.DrawLine(_secondaryYs.AsSpan(0, columns), BrailleLayer.Secondary);
			}
		}

		if (PlanGhost(index, map, columns))
		{
			_braille.DrawLine(_secondaryYs.AsSpan(0, columns), BrailleLayer.Ghost);
		}

		_planner.Plan(channel.Primary, map, _settings.GapSeconds);
		_planner.SampleColumns(_primaryYs.AsSpan(0, columns));
		if (!stacked)
		{
			_braille.DrawFill(_primaryYs.AsSpan(0, columns));
		}

		_braille.DrawLine(_primaryYs.AsSpan(0, columns), BrailleLayer.Primary);
		_braille.WriteTo(_part, slot.PlotRow, layout.Left, slot.Kind);
	}

	/// <summary>
	/// The moment under the mouse (or frozen) in a flyout next to it, over the graphs, so the side panel keeps the live
	/// picture; T shows the moment in the side panel instead. It is written after the plots, and again whenever a plot
	/// under it was sent; where it was before, the graphs are sent again.
	/// </summary>
	/// <summary>
	/// Decides where the flyout of this frame goes and what it says, before the graphs are drawn: a Sixel graph under it
	/// is sent around it, because Konsole deletes an image when text is written into its first row.
	/// </summary>
	private void PlaceFlyout(TerminalLayout layout)
	{
		_flyoutPlan = null;
		var lines = !_settings.FocusInPanel && _menu is null && _focusSample is { } sample ? InfoPanel.BuildFocus(sample, FocusOn(sample)) : null;
		var width = Math.Min(FlyoutWidth, layout.Width - 4);
		var top = layout.Slots.Count > 0 ? layout.Slots[0].HeaderRow : 0;
		var rows = Math.Min((lines?.Count ?? 0) + 2, layout.FooterRow - top);
		if (lines is null || lines.Count == 0 || width < 24 || rows < 4)
		{
			ClearFlyout();
			return;
		}

		var fitted = InfoPanel.Fit(lines, rows - 2);
		var anchor = double.IsNaN(_pinTime) ? _mouseCell : _pinCell;
		var column = anchor.Column + 3 + width <= layout.Left + layout.Width ? anchor.Column + 3 : anchor.Column - 3 - width;
		column = Math.Clamp(column, layout.Left, layout.Left + layout.Width - width);
		var row = Math.Clamp(anchor.Row + 1, top, layout.FooterRow - rows);
		var area = (row, column, rows, width);
		if (_flyout is { } previous && previous != area)
		{
			InvalidateUnder(previous);
		}

		_flyoutPlan = (area, fitted, string.Join('\n', fitted));
	}

	/// <summary>Writes the flyout placed by <see cref="PlaceFlyout"/> when it is new, moved, changed, or something was drawn under it.</summary>
	private bool DrawFlyout(bool redrawn)
	{
		if (_flyoutPlan is not { } plan)
		{
			return false;
		}

		var (area, fitted, key) = plan;
		if (!redrawn && _flyout == area && key == _flyoutKey)
		{
			return false;
		}

		_flyout = area;
		_flyoutKey = key;
		var (row, column, rows, width) = area;
		var labelWidths = InfoPanel.LabelWidths(fitted, width - 2);
		for (var i = 0; i < rows; i++)
		{
			_frame.MoveTo(row + i, column).Background(FlyoutBackground).Blank(width);
			if (i >= 1 && i - 1 < fitted.Count)
			{
				PanelView.WriteLine(_frame, row + i, column + 1, width - 2, fitted[i - 1], labelWidths[i - 1], FlyoutBackground, erase: false);
			}
		}

		return true;
	}

	/// <summary>Takes the flyout away: the graphs under it are sent again with the next frame.</summary>
	private void ClearFlyout()
	{
		if (_flyout is { } previous)
		{
			InvalidateUnder(previous);
			_flyout = null;
			_flyoutKey = string.Empty;
		}
	}

	/// <summary>Makes the next frame erase <paramref name="area"/> and send every header, plot, panel row and footer under it again.</summary>
	private void InvalidateUnder((int Row, int Column, int Rows, int Width) area)
	{
		_erase.Add(area);

		foreach (var slot in _layout!.Slots)
		{
			var last = Math.Max(slot.HeaderRow, slot.PlotRow + slot.PlotRows - 1);
			if (slot.HeaderRow <= area.Row + area.Rows - 1 && last >= area.Row)
			{
				var index = _shown[slot.Index];
				_lastHeader[index] = string.Empty;
				_lastPlotLength[index] = 0;
				_lastPixelCount[index] = 0;
			}
		}

		_headersDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>Sends a plot only when it differs from what is on screen; a flat trace often does not change.</summary>
	private bool AppendIfChanged(int index)
	{
		var bytes = _part.Written;
		if (bytes.IsEmpty || bytes.SequenceEqual(_lastPlot[index].AsSpan(0, _lastPlotLength[index])))
		{
			return false;
		}

		if (_lastPlot[index].Length < bytes.Length)
		{
			_lastPlot[index] = new byte[bytes.Length * 2];
		}

		bytes.CopyTo(_lastPlot[index]);
		_lastPlotLength[index] = bytes.Length;
		_frame.Raw(bytes);
		return true;
	}

	/// <summary>
	/// Writes the rows of the info panel that changed. The content is cut to the panel's rows, so it never runs
	/// past the bottom of the terminal; uptime and process counts make it change about once per sample.
	/// </summary>
	private void DrawPanel(ConsoleHost host)
	{
		_panelDirty = false;
		var layout = _layout!;
		if (layout.PanelWidth == 0)
		{
			return;
		}

		// The moment under the mouse (or frozen) when there is one, else the right edge of the view.
		var focusSample = _settings.FocusInPanel ? _focusSample : null;
		var sample = focusSample ?? ViewSample();
		TimeSpan? uptime = sample is { UptimeSeconds: var seconds } && double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : null;
		var edge = Scrolled ? ViewTime() - _settings.DisplayDelaySeconds : double.PositiveInfinity;
		var focus = focusSample is null ? null : FocusOn(focusSample);
		var lines = InfoPanel.Arrange(
			InfoPanel.Build(sample?.Details, sample, uptime, _events.Latest(InfoPanel.MaximumEvents, edge), focus),
			["MOMENT", "IN VIEW", .. _settings.PanelOrder],
			_settings.HiddenSections);
		_panelTitles = [.. InfoPanel.Sections(lines).Select(section => section.Title).Where(title => title is not ("MOMENT" or "IN VIEW"))];
		var extra = new List<PanelLine>();
		if (HasSelection)
		{
			extra.AddRange(RangePanel.Selection(SummaryOf(_selectFrom, _selectTo)));
		}

		if (_baseline is not null && CompareTarget() is { } target)
		{
			extra.AddRange(RangePanel.Baseline(_baseline.Summary, target));
		}

		if (_settings.StackCpu && _shown.Any(index => _graphs[index].Kind == MetricKind.Cpu))
		{
			var right = ViewRightTime();
			var window = SamplesBetween(right - _settings.HistorySeconds, right).Select(item => item.CpuPercent).Where(double.IsFinite).ToList();
			extra.AddRange(RangePanel.Stack(StackOf().Names, window.Count > 0 ? window.Average() : double.NaN));
		}

		lines.InsertRange(0, extra);
		if (_recording is not null)
		{
			lines.InsertRange(0, InfoPanel.BuildReplay(Path.GetFileName(_recording.FilePath), _recording.Start, _recording.End, sample?.RecordedAt));
		}

		// A wide panel spreads its sections over two columns; every column gets a label column that fits its own labels.
		var count = TerminalLayout.PanelColumnsFor(layout.PanelWidth);
		var width = count == 1 ? layout.PanelWidth : (layout.PanelWidth - TerminalLayout.PanelGap) / 2;
		var columns = InfoPanel.FitColumns(lines, layout.PanelRows, count);
		_frame.Clear();
		for (var column = 0; column < columns.Count; column++)
		{
			var rows = columns[column];
			var labelWidths = InfoPanel.LabelWidths(rows, width);
			var left = layout.PanelLeft + (column * (width + TerminalLayout.PanelGap));
			for (var row = 0; row < layout.PanelRows; row++)
			{
				var line = row < rows.Count ? rows[row] : new PanelLine(PanelLineKind.Blank, string.Empty, string.Empty, 0);
				var labelWidth = row < rows.Count ? labelWidths[row] : InfoPanel.LabelWidth;
				if (_lastPanel[column][row] is { } previous && previous.Line.Equals(line) && previous.LabelWidth == labelWidth)
				{
					continue;
				}

				_lastPanel[column][row] = (line, labelWidth);
				var lit = _panelHover == (column, row) && IsClickable(line);
				PanelView.WriteLine(_frame, layout.PanelTop + row, left, width, line, labelWidth, lit ? MenuSelection : null);
			}
		}

		if (_frame.Length > 0)
		{
			host.Write(_frame.Written);
			if (_menu is not null)
			{
				// The panel was written over the menu where they meet: the menu goes on top again.
				_menuKey = string.Empty;
				RedrawWhilePaused();
			}
		}
	}

	private void DrawFooter(ConsoleHost host)
	{
		_frame.Clear();
		if (AppendFooter())
		{
			host.Write(_frame.Written);
		}
	}

	private bool AppendFooter()
	{
		var layout = _layout!;
		var now = MonotonicClock.Now;
		if (_toast is not null && now > _toastUntil)
		{
			_toast = null;
		}

		// Most actionable first: a frozen moment, a selection, a zoom, a baseline, a view held back or paused; the state last.
		var machine = _info.Host is { } host ? $"{host} · " : string.Empty;
		var flags = $"{(_recorder is null ? string.Empty : "● rec · ")}{(_notifier is null ? string.Empty : "notify · ")}";
		var parts = new List<string>();
		if (!double.IsNaN(_pinTime))
		{
			parts.Add(FrozenText());
		}

		if (HasSelection)
		{
			parts.Add(Messages.Selection(_selectTo - _selectFrom));
		}

		if (_zooms.Count > 0)
		{
			parts.Add("Backspace zoom out");
		}

		if (_baseline is not null && !HasSelection)
		{
			parts.Add(Messages.Baseline(_baseline.To - _baseline.From));
		}

		if (_recording is not null)
		{
			var at = ViewSample()?.RecordedAt?.LocalDateTime.ToString("T", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
			parts.Add($"{(_playing ? "▶ Playing" : "Replay")} {Path.GetFileName(_recording.FilePath)} · {at} · {Units.Duration(_settings.HistorySeconds)}");
		}
		else
		{
			if (Scrolled)
			{
				parts.Add(Messages.Back(Math.Max(0, EdgeTime() - _viewEnd)));
			}

			if (_paused)
			{
				parts.Add(Messages.Paused);
			}

			parts.Add($"{flags}{machine}{Units.Duration(_settings.HistorySeconds)} · every {Units.Duration(_settings.IntervalSeconds)} · {(_sixel ? "sixel" : "braille")}");
		}

		var status = _toast ?? string.Join(" · ", parts);
		var hints = _settings.Compact ? string.Empty : _hints;
		var width = layout.Columns - (2 * layout.Left);
		var footer = $"{status}|{layout.Width}|{_footerHover}|{hints.Length}";
		if (footer == _lastFooter)
		{
			return false;
		}

		_lastFooter = footer;
		var line = HeaderLine.FooterLine(width, hints, status);
		if (line != _footerLine)
		{
			// What the mouse points at moved: it is lit again from the next move.
			_footerLine = line;
			_footerHover = null;
		}

		// The footer spans the whole terminal, under the graphs and the info panel.
		HeaderLine.WriteFooter(_frame, layout.FooterRow, layout.Left, width, hints, status, _paused || Scrolled || _toast is not null || HasSelection || !double.IsNaN(_pinTime), _footerHover, MenuSelection);
		return true;
	}

	/// <summary>
	/// The overview, from <see cref="KeyMap"/>: the keys, the mouse and the marker colours by group, or (? or Tab) what can
	/// be typed in a shell. The rows run down in as many columns as the height needs, texts cut to the width.
	/// </summary>
	private void WriteHelp()
	{
		var rows = new List<(string Keys, string Action, EventKind? Marker, bool Title)>();
		if (_helpPage == 0)
		{
			foreach (var group in _keys)
			{
				if (rows.Count > 0)
				{
					rows.Add((string.Empty, string.Empty, null, false));
				}

				rows.Add((group.Title.ToUpperInvariant(), string.Empty, null, true));
				rows.AddRange(group.Rows.Select(row => (row.Keys, row.Action, row.Marker, false)));
			}
		}
		else
		{
			rows.AddRange(KeyMap.ShellCommands.Select(row => (row.Keys, row.Action, (EventKind?) null, false)));
		}

		const int Gap = 4;
		var available = Math.Max(4, _rows - 6);
		var count = Math.Clamp((rows.Count + available - 1) / available, 1, 3);
		var perColumn = (rows.Count + count - 1) / count;
		var keysWidth = rows.Where(row => !row.Title).Max(row => row.Keys.Length + (row.Marker is null ? 0 : 2));
		var columnWidth = Math.Min(keysWidth + 3 + rows.Max(row => row.Action.Length), (_columns - 2 - (Gap * (count - 1))) / count);
		keysWidth = Math.Min(keysWidth, columnWidth / 2);
		var total = (columnWidth * count) + (Gap * (count - 1));
		var left = Math.Max(0, (_columns - total) / 2);
		var top = Math.Max(0, (_rows - perColumn - 4) / 2);
		_frame.MoveTo(top, left).Bold().Foreground(ScopeColors.TextPrimary).Text(_helpPage == 0 ? "KEYS, MOUSE AND MARKERS" : "FROM THE SHELL").NotBold()
			.Foreground(ScopeColors.TextDim).Text(_helpPage == 0 ? "   ? or Tab: the shell commands" : "   ? or Tab: keys, mouse and markers");
		for (var i = 0; i < rows.Count; i++)
		{
			var (keys, action, marker, title) = rows[i];
			var column = left + (i / perColumn * (columnWidth + Gap));
			var row = top + 2 + (i % perColumn);
			_frame.MoveTo(row, column);
			if (title)
			{
				_frame.Bold().Foreground(ScopeColors.TextSecondary).Text(HeaderLine.Fit(keys, columnWidth)).NotBold();
				continue;
			}

			if (marker is { } kind)
			{
				_frame.Foreground(ScopeColors.MarkerOf(kind)).Text("│ ");
			}

			_frame.Foreground(ScopeColors.TextPrimary).Text(HeaderLine.Fit(keys, keysWidth - (marker is null ? 0 : 2)))
				.MoveTo(row, column + keysWidth + 3).Foreground(ScopeColors.TextSecondary).Text(HeaderLine.Fit(action, columnWidth - keysWidth - 3));
		}

		_frame.MoveTo(top + 3 + perColumn, left).Foreground(ScopeColors.TextDim).Text("? or Tab turns the page · any other key goes back");
	}

	private void EnsureColumns(int columns)
	{
		if (_primaryYs.Length < columns)
		{
			_primaryYs = new double[columns];
			_secondaryYs = new double[columns];
		}
	}

	// ---- Keys ------------------------------------------------------------------------------------------

	private void Handle(KeyPress key)
	{
		if (_menu is not null)
		{
			HandleMenuKey(key);
			return;
		}

		if (_help && _helpPage == LayoutPage)
		{
			HandleLayoutKey(key);
			return;
		}

		if (_help)
		{
			// ? or Tab turns the page between the keys and the shell commands; any other key goes back.
			if (key.Character == '?' || key.VirtualKey is TabKey or 0x70)
			{
				_helpPage = 1 - _helpPage;
			}
			else
			{
				_help = false;
			}

			_layoutDirty = true;
			return;
		}

		if (key.Character == '\u0003' || (key.Control && key.VirtualKey is 'C' or 'Q'))
		{
			_quit = true;
			return;
		}

		if (key.Control || key.Alt)
		{
			return;
		}

		if (key.Character == '?' || key.VirtualKey == 0x70)
		{
			_help = true;
			_helpPage = 0;
			_layoutDirty = true;
			return;
		}

		for (var index = 0; index < _graphs.Count; index++)
		{
			var digit = Graph.KeyOf(index);
			if (key.VirtualKey == digit || key.VirtualKey == 0x60 + (digit - '0'))
			{
				var graph = _graphs[index];
				if (key.Shift)
				{
					graph.SetSize(graph.NextSize());
				}
				else
				{
					graph.SetVisible(!graph.Visible);
				}

				_layoutDirty = true;
				return;
			}
		}

		switch (key.VirtualKey)
		{
			case 'Q':
				_quit = true;
				break;
			case ' ':
				TogglePause();
				break;
			case LeftKey:
				Scroll(key.Shift ? _settings.HistorySeconds : _settings.HistorySeconds / 4);
				break;
			case RightKey:
				Scroll(-(key.Shift ? _settings.HistorySeconds : _settings.HistorySeconds / 4));
				break;
			case HomeKey:
				Scroll(double.PositiveInfinity);
				break;
			case EndKey:
				Scroll(double.NegativeInfinity);
				break;
			case 'H':
				SetHistory(PulseSettings.NextPreset(_historyPresets, _settings.HistorySeconds));
				break;
			case 'R' when _recording is null:
				SetInterval(PulseSettings.NextPreset(PulseSettings.IntervalPresets, _settings.IntervalSeconds));
				break;
			case 'K':
				ToggleCores();
				break;
			case 'P':
				Change(settings => settings.ShowMinMax = !settings.ShowMinMax);
				_headersDirty = true;
				break;
			case 'M':
				Change(settings => settings.ShowMarkers = !settings.ShowMarkers);
				Toast(Messages.Switch("Markers", _settings.ShowMarkers));
				RedrawWhilePaused();
				break;
			case 'N' when _recording is null:
				ToggleNotifications();
				break;
			case 'C':
				Change(settings => settings.Compact = !settings.Compact);
				_layoutDirty = true;
				break;
			case 'I':
				Change(settings => settings.ShowPanel = !settings.ShowPanel);
				_layoutDirty = true;
				break;
			case EscapeKey when !double.IsNaN(_pinTime):
				LetGo();
				break;
			case MenuKey:
			case F10Key when key.Shift:
				OpenMenuAtCursor();
				break;
			case 'L':
				_help = true;
				_helpPage = LayoutPage;
				_layoutCursor = 0;
				_layoutDirty = true;
				break;
			case EscapeKey when HasSelection:
				ClearSelection();
				break;
			case EnterKey when HasSelection:
				ZoomToSelection();
				break;
			case BackspaceKey when _zooms.Count > 0:
				ZoomOut();
				break;
			case 'E' when HasSelection:
				ExportSelection();
				break;
			case 'Y' when HasSelection:
				CopySelection();
				break;
			case 'B':
				ToggleBaseline();
				break;
			case 'S':
				ToggleStack();
				break;
			case 'D':
				Change(settings => settings.FocusInPanel = !settings.FocusInPanel);
				_panelDirty = true;
				Toast(Messages.Details(_settings.FocusInPanel));
				RedrawWhilePaused();
				break;
			case 'F':
				_maxFps = _maxFps >= 60 ? 30 : 60;
				Toast(Messages.FrameRate(_maxFps));
				break;
		}
	}

	// ---- Selection, zoom and baseline ------------------------------------------------------------------

	/// <summary>Whether a range of time is selected (dragged across a plot).</summary>
	private bool HasSelection => _selectTo > _selectFrom;

	private void ClearSelection()
	{
		_selectFrom = double.NaN;
		_selectTo = double.NaN;
		_panelDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>The history becomes the selected range (10 seconds at least), held at its end; Backspace goes back.</summary>
	private void ZoomToSelection()
	{
		_zooms.Push((_settings.HistorySeconds, _viewEnd));
		var length = Math.Max(10, _selectTo - _selectFrom);
		_settings.HistorySeconds = length; // A zoom is not remembered: it is rarely one of the presets.
		var end = _selectFrom + length + _settings.DisplayDelaySeconds;
		_viewEnd = end >= EdgeTime() - 0.05 ? double.NaN : end;
		_stack = null;
		RefreshChannels();
		Toast(Messages.Zoomed(length));
		_panelDirty = true;
		RedrawWhilePaused();
	}

	private void ZoomOut()
	{
		var (history, viewEnd) = _zooms.Pop();
		_settings.HistorySeconds = history;
		_viewEnd = viewEnd < EdgeTime() - 0.05 ? viewEnd : double.NaN;
		_stack = null;
		RefreshChannels();
		Toast(Messages.History(history));
		_panelDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>The samples with a time in [<paramref name="from"/>, <paramref name="to"/>], oldest first.</summary>
	private List<MetricSnapshot> SamplesBetween(double from, double to)
	{
		var samples = new List<MetricSnapshot>();
		for (var i = 0; i < _history.Count; i++)
		{
			if (_history[i].Time >= from && _history[i].Time <= to)
			{
				samples.Add(_history[i]);
			}
		}

		return samples;
	}

	/// <summary>The wall-clock time of a moment: from the recording when one plays, else from the sample clock.</summary>
	private DateTimeOffset WallAt(double time) => _recording is not null && SampleAt(time)?.RecordedAt is { } at ? at : WallTimeOf(time);

	/// <summary>The shown graphs, the busiest processes and the events over [<paramref name="from"/>, <paramref name="to"/>].</summary>
	private RangeSummary SummaryOf(double from, double to)
	{
		var samples = SamplesBetween(from, to);
		var graphs = _shown.Select(index => _graphs[index].Channel)
			.Select(channel => new RangeGraph(ChannelText.TitleOf(channel), RangeStats.Of(channel.Primary, from, to), value => ChannelText.Format(channel, value), IsPercent(channel)))
			.ToList();
		return new RangeSummary(
			WallAt(from),
			WallAt(to),
			graphs,
			ProcessCausation.AverageCpu(samples, RangePanel.MaximumCpu),
			ProcessCausation.AverageIo(samples, RangePanel.MaximumIo),
			[.. _events.Between(from, to)]);
	}

	/// <summary>Whether the main trace of <paramref name="channel"/> is a percentage, so it is compared in points.</summary>
	private static bool IsPercent(Channel channel)
		=> channel.Kind is MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory or MetricKind.Process || channel.Watch?.Counter?.Unit == CounterUnit.Percent;

	/// <summary>Saves the samples of the selection as a CSV recording in Documents\Pulse, which <c>-replay</c> plays.</summary>
	private void ExportSelection()
	{
		var samples = SamplesBetween(_selectFrom, _selectTo);
		if (samples.Count == 0)
		{
			Toast(Messages.SelectionEmpty);
			return;
		}

		try
		{
			var file = CsvRecorder.DefaultPath(_info.Host);
			var path = Path.Combine(Path.GetDirectoryName(file)!, "selection-" + Path.GetFileName(file));
			using (var recorder = new CsvRecorder(path, _info.Watches))
			{
				foreach (var sample in samples)
				{
					recorder.Write(sample, WallAt(sample.Time));
				}
			}

			Log.Info($"Saved {samples.Count} samples of a selection to {path}.");
			Toast(Messages.Saved(samples.Count, path), 8);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Toast(Messages.SaveFailed(ex.Message), 6);
		}
	}

	/// <summary>Copies the summary of the selection (and the comparison with the baseline) to the clipboard as text.</summary>
	private void CopySelection()
	{
		Copy(RangePanel.Report(SummaryOf(_selectFrom, _selectTo), _baseline?.Summary, _info.Host));
	}

	/// <summary>
	/// B: the selection becomes the baseline, which the panel compares every later selection (or the visible window) to
	/// and which is drawn faintly behind the traces; B without a selection clears it.
	/// </summary>
	private void ToggleBaseline()
	{
		if (!HasSelection)
		{
			if (_baseline is null)
			{
				Toast(Messages.BaselineNeedsSelection, 4);
				return;
			}

			_baseline = null;
			Toast(Messages.BaselineCleared);
		}
		else
		{
			var traces = new Series?[_graphs.Count];
			for (var index = 0; index < _graphs.Count; index++)
			{
				var primary = _graphs[index].Channel.Primary;
				var first = primary.LowerBound(_selectFrom);
				var copy = new Series(Math.Max(2, primary.Samples.Count - first));
				for (var i = first; i < primary.Samples.Count && primary.Samples[i].Time <= _selectTo; i++)
				{
					copy.Add(primary.Samples[i].Time, primary.Samples[i].Value);
				}

				traces[index] = copy;
			}

			_baseline = new Baseline(_selectFrom, _selectTo, SummaryOf(_selectFrom, _selectTo), traces);
			Toast(Messages.BaselineSet(_selectTo - _selectFrom), 4);
		}

		_panelDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>What the baseline is compared to: another selection, else the visible window.</summary>
	private RangeSummary? CompareTarget()
	{
		if (_baseline is null)
		{
			return null;
		}

		if (HasSelection && (_selectFrom != _baseline.From || _selectTo != _baseline.To))
		{
			return SummaryOf(_selectFrom, _selectTo);
		}

		var right = ViewRightTime();
		return SummaryOf(right - _settings.HistorySeconds, right);
	}

	/// <summary>The moment at the right edge of the plots, on the sample clock.</summary>
	private double ViewRightTime() => (_paused || Scrolled ? ViewTime() : MonotonicClock.Now) - _settings.DisplayDelaySeconds;

	/// <summary>
	/// How far the baseline is moved to be drawn: its start at the start of another selection, else its end at the right
	/// edge of the plots.
	/// </summary>
	private double GhostShift(double rightTime)
	{
		var baseline = _baseline!;
		var start = HasSelection && _selectFrom != baseline.From ? _selectFrom : rightTime - (baseline.To - baseline.From);
		return start - baseline.From;
	}

	/// <summary>The bands of the stacked CPU graph for the visible window, worked out once per sample or view change.</summary>
	private CpuStack StackOf()
	{
		if (_stack is null)
		{
			var right = ViewRightTime();
			_stack = CpuStack.Of(SamplesBetween(right - _settings.HistorySeconds, right));
		}

		return _stack;
	}

	/// <summary>A range kept to compare against: its summary, and a copy of the main trace of every graph over it.</summary>
	private sealed record Baseline(double From, double To, RangeSummary Summary, Series?[] Traces);

	/// <summary>Applies a change made with the keys to this launch and to the remembered settings.</summary>
	private void Change(Action<PulseSettings> apply)
	{
		apply(_settings);
		apply(_saved);
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

		Toast(Messages.Switch("Notifications", _settings.Notify));
	}

	/// <summary>
	/// Moves the view back (positive) or forward (negative) by <paramref name="seconds"/> and holds it there while
	/// sampling goes on; it stops at the oldest sample, and reaching the newest moment follows it live again.
	/// </summary>
	private void Scroll(double seconds)
	{
		_playing = false;
		var edge = EdgeTime();
		var end = Math.Max(ViewTime() - seconds, Math.Min(OldestViewTime(), edge));
		_viewEnd = end >= edge - 0.05 ? double.NaN : end;
		AfterViewChange();
	}

	/// <summary>A recording plays forward: one window of history every <see cref="PlaySecondsPerWindow"/> seconds.</summary>
	private void Play(double deltaSeconds)
	{
		_viewEnd += deltaSeconds * _settings.HistorySeconds / PlaySecondsPerWindow;
		if (!(_viewEnd < EdgeTime()))
		{
			_viewEnd = double.NaN;
			_playing = false;
		}

		AfterViewChange();
	}

	private void AfterViewChange()
	{
		_stack = null;
		RefreshChannels();
		_panelDirty = true;
		RedrawWhilePaused();
	}

	private void TogglePause()
	{
		if (_recording is not null)
		{
			// A recording plays instead: from the start when the view is at its end.
			_playing = !_playing;
			if (_playing && !Scrolled)
			{
				var start = OldestViewTime();
				_viewEnd = start < _frozenTime - 0.05 ? start : double.NaN;
				_playing = Scrolled;
			}

			_nextFrame = 0;
			return;
		}

		// Looking back: Space returns to live (and resumes, when paused too).
		if (Scrolled)
		{
			_viewEnd = double.NaN;
			if (!_paused)
			{
				AfterViewChange();
				return;
			}
		}

		_paused = !_paused;
		_sampler!.IsPaused = _paused;
		if (_paused)
		{
			_frozenTime = MonotonicClock.Now;
			return;
		}

		var now = MonotonicClock.Now;
		foreach (var graph in _graphs)
		{
			graph.Channel.AppendBreak(now);
		}

		RefreshChannels();
		_panelDirty = true;
		_nextFrame = 0;
	}

	private void SetHistory(double seconds)
	{
		if (_recording is null)
		{
			Change(settings => settings.HistorySeconds = seconds);
		}
		else
		{
			_settings.HistorySeconds = seconds; // Zooming a recording is not remembered: it may exceed the live presets.
		}

		if (Scrolled)
		{
			var end = Math.Max(_viewEnd, Math.Min(OldestViewTime(), EdgeTime()));
			_viewEnd = end >= EdgeTime() - 0.05 ? double.NaN : end;
		}

		RefreshChannels();
		Toast(Messages.History(seconds));
		RedrawWhilePaused();
	}

	private void SetInterval(double seconds)
	{
		Change(settings => settings.IntervalSeconds = seconds);
		_sampler!.Interval = TimeSpan.FromSeconds(seconds);
		RefreshChannels();
		Toast(Messages.Sampling(seconds));
		RedrawWhilePaused();
	}

	/// <summary>While paused the picture is frozen at the pause time, but a changed setting still redraws it.</summary>
	private void RedrawWhilePaused() => _frameDirty = true;

	private void Toast(string message, double seconds = 2)
	{
		_toast = message;
		_toastUntil = MonotonicClock.Now + seconds;
	}
}
