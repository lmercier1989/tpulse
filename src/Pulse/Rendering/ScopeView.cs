using System.Windows;
using System.Windows.Media;

using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Rendering;

/// <summary>
/// The trace area of one graph, drawn with retained WPF visuals (DirectX, anti-aliased, sub-pixel).
/// <para>
/// The waveform geometry is rebuilt only when a sample arrives, the layout changes or the scale eases to a
/// new step. Between those moments a frame only moves a <see cref="TranslateTransform"/> by
/// <c>elapsed seconds x pixels per second</c>, so the trace scrolls continuously at sub-pixel precision
/// while the managed side does almost no work per frame.
/// </para>
/// <para>
/// The right edge shows the time "now minus the display delay". The delay is two sampling intervals, so
/// every curve segment is final before it scrolls into view (a monotone cubic segment depends on the
/// samples on both sides of it); a beam dot marks where the trace meets the edge.
/// </para>
/// </summary>
internal sealed class ScopeView : FrameworkElement
{
	private const double TopInset = 3;
	private const double BottomInset = 1.5;
	private const double HeadRadius = 2.3;
	private const double HaloRadius = 5.5;
	private const int VerticalDivisions = 10;
	private const int HorizontalDivisions = 4;

	private readonly VisualCollection _children;
	private readonly DrawingVisual _gridVisual = new();
	private readonly ContainerVisual _plotVisual = new();
	private readonly DrawingVisual _waveVisual = new();
	private readonly DrawingVisual _primaryHead = new();
	private readonly DrawingVisual _secondaryHead = new();
	private readonly TranslateTransform _waveShift = new();
	private readonly TranslateTransform _primaryHeadShift = new();
	private readonly TranslateTransform _secondaryHeadShift = new();
	private readonly CurvePlanner _primaryCurve = new();
	private readonly CurvePlanner _secondaryCurve = new();
	private readonly List<CurvePlanner> _coreCurves = [];

	private Channel? _channel;
	private ScopeStyle? _style;
	private Brush? _fill;
	private double _historySeconds = 60;
	private double _delaySeconds = 2.15;
	private double _gapSeconds = 6;
	private bool _showCores;
	private bool _showMarkers = true;
	private EventHistory? _events;
	private int _builtEvents = -1;
	private readonly Dictionary<EventKind, Pen> _markerPens = [];
	private double _desiredPlotHeight = 96;
	private double _builtAt;
	private int _builtVersion = -1;
	private bool _dirty = true;
	private double _pixelsPerSecond;

	public ScopeView()
	{
		_plotVisual.Children.Add(_waveVisual);
		_waveVisual.Transform = _waveShift;
		_primaryHead.Transform = _primaryHeadShift;
		_secondaryHead.Transform = _secondaryHeadShift;
		_primaryHead.Opacity = 0;
		_secondaryHead.Opacity = 0;
		_children = new VisualCollection(this) { _gridVisual, _plotVisual, _secondaryHead, _primaryHead };
	}

	/// <summary>The plot height this graph asks for; star rows scale it when the window is resized.</summary>
	public double DesiredPlotHeight
	{
		get => _desiredPlotHeight;
		set
		{
			if (_desiredPlotHeight != value)
			{
				_desiredPlotHeight = value;
				InvalidateMeasure();
			}
		}
	}

	protected override int VisualChildrenCount => _children.Count;

	public void Attach(Channel channel, ScopeStyle style)
	{
		_channel = channel;
		_style = style;
		DrawHead(_primaryHead, style.PrimaryBrush, style.PrimaryHalo);
		DrawHead(_secondaryHead, style.SecondaryBrush, style.SecondaryHalo);
		_dirty = true;
	}

	public void Configure(double historySeconds, double delaySeconds, double gapSeconds, bool showCores, bool showMarkers)
	{
		_historySeconds = historySeconds;
		_delaySeconds = delaySeconds;
		_gapSeconds = gapSeconds;
		_showCores = showCores;
		_showMarkers = showMarkers;
		_dirty = true;
	}

	/// <summary>The events drawn as markers across the plot (git activity, Windows errors).</summary>
	public void ShowEvents(EventHistory events)
	{
		_events = events;
		_dirty = true;
	}

	/// <summary>Advances one frame to time <paramref name="now"/> (seconds, <see cref="Core.Diagnostics.MonotonicClock"/>).</summary>
	public void Tick(double now, double deltaSeconds)
	{
		if (_channel is null || _style is null)
		{
			return;
		}

		var width = ActualWidth;
		var height = ActualHeight;
		if (width < 4 || height < 8)
		{
			return;
		}

		var scaling = _channel.Scale.Advance(deltaSeconds);
		if (_dirty || scaling || _builtVersion != _channel.Version || (_events is not null && _builtEvents != _events.Version))
		{
			Rebuild(now, width, height);
		}

		var shift = -(now - _builtAt) * _pixelsPerSecond;
		_waveShift.X = shift;
		PlaceHead(_primaryCurve, _primaryHead, _primaryHeadShift, shift, width);
		if (_channel.Secondary is not null)
		{
			PlaceHead(_secondaryCurve, _secondaryHead, _secondaryHeadShift, shift, width);
		}
	}

	protected override Visual GetVisualChild(int index) => _children[index];

	protected override Size MeasureOverride(Size availableSize)
		=> new(0, double.IsInfinity(availableSize.Height) ? _desiredPlotHeight : Math.Min(_desiredPlotHeight, availableSize.Height));

	protected override Size ArrangeOverride(Size finalSize)
	{
		_fill = null;
		_dirty = true;
		_plotVisual.Clip = new RectangleGeometry(new Rect(0, -2, finalSize.Width, finalSize.Height + 4));
		DrawGrid(finalSize);
		return finalSize;
	}

	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		base.OnDpiChanged(oldDpi, newDpi);
		DrawGrid(RenderSize);
		_dirty = true;
	}

	private static void DrawHead(DrawingVisual head, Brush core, Brush halo)
	{
		using var drawing = head.RenderOpen();
		drawing.DrawEllipse(halo, null, default, HaloRadius, HaloRadius);
		drawing.DrawEllipse(core, null, default, HeadRadius, HeadRadius);
	}

	private static void PlaceHead(CurvePlanner curve, DrawingVisual head, TranslateTransform transform, double shift, double width)
	{
		// The edge in the coordinates the geometry was built in.
		var edge = width - shift;
		var visible = curve.TryGetEnd(out var x, out var y);
		if (visible && x > edge)
		{
			x = edge;
			visible = curve.TryGetY(edge, out y);
		}

		var screenX = x + shift;
		visible &= screenX >= 0;
		var opacity = visible ? 1.0 : 0.0;
		if (head.Opacity != opacity)
		{
			head.Opacity = opacity;
		}

		if (visible)
		{
			transform.X = screenX;
			transform.Y = y;
		}
	}

	private static StreamGeometry Build(CurvePlanner curve, bool filled, double baseline)
	{
		var geometry = new StreamGeometry();
		using (var context = geometry.Open())
		{
			var sink = new StreamGeometrySink(context);
			curve.Emit(ref sink, filled, baseline);
		}

		geometry.Freeze();
		return geometry;
	}

	private static double SnapToPixelCenter(double value, double scale) => (Math.Floor(value * scale) + 0.5) / scale;

	private static Pen Hairline(Color color, double scale)
	{
		var pen = new Pen(new SolidColorBrush(color), 1 / scale);
		pen.Freeze();
		return pen;
	}

	private void Rebuild(double now, double width, double height)
	{
		var channel = _channel!;
		var style = _style!;
		var top = TopInset;
		var bottom = height - BottomInset;
		_pixelsPerSecond = width / _historySeconds;
		var map = new PlotMapping(now - _delaySeconds, width, _pixelsPerSecond, top, bottom, (bottom - top) / channel.Scale.Current);
		_fill ??= style.CreateFill(top, bottom);

		using (var drawing = _waveVisual.RenderOpen())
		{
			// Markers first, so the traces stay on top; events newer than the edge scroll in with the wave.
			if (_showMarkers && _events is not null)
			{
				var title = ChannelText.TitleOf(channel);
				foreach (var item in _events.Between(map.TimeAt(0) - 5, double.PositiveInfinity))
				{
					if (item.BelongsTo(title))
					{
						var x = map.X(item.Time);
						drawing.DrawLine(MarkerPen(item.Kind), new Point(x, top), new Point(x, bottom));
					}
				}
			}

			if (_showCores && channel.Cores.Length > 0)
			{
				drawing.DrawGeometry(null, style.CorePen, BuildCores(channel.Cores, map, bottom));
			}

			if (channel.Secondary is not null)
			{
				_secondaryCurve.Plan(channel.Secondary, map, _gapSeconds);
				drawing.DrawGeometry(null, style.SecondaryPen, Build(_secondaryCurve, filled: false, bottom));
			}

			_primaryCurve.Plan(channel.Primary, map, _gapSeconds);
			drawing.DrawGeometry(_fill, style.PrimaryPen, Build(_primaryCurve, filled: true, bottom));
		}

		_builtAt = now;
		_builtVersion = channel.Version;
		_builtEvents = _events?.Version ?? -1;
		_dirty = false;
	}

	/// <summary>A dashed hairline in the colour of the event kind, like the markers in the terminal.</summary>
	private Pen MarkerPen(EventKind kind)
	{
		if (!_markerPens.TryGetValue(kind, out var pen))
		{
			pen = new Pen(new SolidColorBrush(Palette.WithAlpha(Palette.MarkerOf(kind), 0.6)), 1) { DashStyle = new DashStyle([4, 2], 0) };
			pen.Freeze();
			_markerPens[kind] = pen;
		}

		return pen;
	}

	private StreamGeometry BuildCores(Series[] cores, in PlotMapping map, double baseline)
	{
		while (_coreCurves.Count < cores.Length)
		{
			_coreCurves.Add(new CurvePlanner());
		}

		var geometry = new StreamGeometry();
		using (var context = geometry.Open())
		{
			var sink = new StreamGeometrySink(context);
			for (var i = 0; i < cores.Length; i++)
			{
				_coreCurves[i].Plan(cores[i], map, _gapSeconds);
				_coreCurves[i].Emit(ref sink, filled: false, baseline);
			}
		}

		geometry.Freeze();
		return geometry;
	}

	/// <summary>A fixed graticule of device-pixel hairlines: four vertical divisions and ten across.</summary>
	private void DrawGrid(Size size)
	{
		using var drawing = _gridVisual.RenderOpen();
		if (size.Width < 4 || size.Height < 8)
		{
			return;
		}

		var dpi = VisualTreeHelper.GetDpi(this);
		var top = TopInset;
		var bottom = size.Height - BottomInset;
		var division = Hairline(Palette.GridDivision, dpi.DpiScaleX);
		var line = Hairline(Palette.GridLine, dpi.DpiScaleY);
		var baseline = Hairline(Palette.Baseline, dpi.DpiScaleY);

		for (var i = 1; i < VerticalDivisions; i++)
		{
			var x = SnapToPixelCenter(size.Width * i / VerticalDivisions, dpi.DpiScaleX);
			drawing.DrawLine(division, new Point(x, top), new Point(x, bottom));
		}

		for (var i = 0; i <= HorizontalDivisions; i++)
		{
			var y = SnapToPixelCenter(top + ((bottom - top) * i / HorizontalDivisions), dpi.DpiScaleY);
			drawing.DrawLine(i == HorizontalDivisions ? baseline : line, new Point(0, y), new Point(size.Width, y));
		}
	}
}
