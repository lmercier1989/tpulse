using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Media.Immutable;

using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Rendering;

/// <summary>
/// The trace area of one graph, drawn by Avalonia (Skia, anti-aliased, sub-pixel).
/// <para>
/// The waveform geometry is rebuilt only when a sample arrives, the layout changes or the scale eases to a
/// new step. Between those moments a frame only moves the waveform by <c>elapsed seconds x pixels per second</c>
/// with a translation, so the trace scrolls continuously at sub-pixel precision while the managed side does almost
/// no work per frame.
/// </para>
/// <para>
/// The right edge shows the time "now minus the display delay". The delay is two sampling intervals, so
/// every curve segment is final before it scrolls into view (a monotone cubic segment depends on the
/// samples on both sides of it); a beam dot marks where the trace meets the edge.
/// </para>
/// </summary>
internal sealed class ScopeView : Control, ICustomHitTest
{
	private const double TopInset = 3;
	private const double BottomInset = 1.5;
	private const double HeadRadius = 2.3;
	private const double HaloRadius = 5.5;
	private const int VerticalDivisions = 10;
	private const int HorizontalDivisions = 4;

	private static readonly IPen DivisionPen = new ImmutablePen(new ImmutableSolidColorBrush(Palette.GridDivision), 1);
	private static readonly IPen LinePen = new ImmutablePen(new ImmutableSolidColorBrush(Palette.GridLine), 1);
	private static readonly IPen BaselinePen = new ImmutablePen(new ImmutableSolidColorBrush(Palette.Baseline), 1);

	private readonly CurvePlanner _primaryCurve = new();
	private readonly CurvePlanner _secondaryCurve = new();
	private readonly List<CurvePlanner> _coreCurves = [];
	private readonly Dictionary<EventKind, IPen> _markerPens = [];
	private readonly List<(double X, EventKind Kind)> _markers = [];

	private Channel? _channel;
	private ScopeStyle? _style;
	private IBrush? _fill;
	private StreamGeometry? _primaryFill;
	private StreamGeometry? _primaryLine;
	private StreamGeometry? _secondaryLine;
	private StreamGeometry? _coreLines;
	private double _historySeconds = 60;
	private double _delaySeconds = 2.15;
	private double _gapSeconds = 6;
	private bool _showCores;
	private bool _showMarkers = true;
	private EventHistory? _events;
	private int _builtEvents = -1;
	private double _desiredPlotHeight = 96;
	private double _builtAt;
	private int _builtVersion = -1;
	private bool _dirty = true;
	private double _pixelsPerSecond;
	private double _top;
	private double _bottom;
	private double _shift;
	private Point? _primaryHead;
	private Point? _secondaryHead;

	public ScopeView() => ClipToBounds = false;

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

	public void Attach(Channel channel, ScopeStyle style)
	{
		_channel = channel;
		_style = style;
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

	/// <summary>The events drawn as markers across the plot (git activity, system errors).</summary>
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

		var width = Bounds.Width;
		var height = Bounds.Height;
		if (width < 4 || height < 8)
		{
			return;
		}

		var scaling = _channel.Scale.Advance(deltaSeconds);
		if (_dirty || scaling || _builtVersion != _channel.Version || (_events is not null && _builtEvents != _events.Version))
		{
			Rebuild(now, width, height);
		}

		_shift = -(now - _builtAt) * _pixelsPerSecond;
		_primaryHead = HeadOf(_primaryCurve, _shift, width);
		_secondaryHead = _channel.Secondary is null ? null : HeadOf(_secondaryCurve, _shift, width);
		InvalidateVisual();
	}

	/// <summary>The whole plot takes the mouse, not only where a line is drawn: the tooltip follows it everywhere.</summary>
	public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

	public override void Render(DrawingContext context)
	{
		var size = Bounds.Size;
		if (size.Width < 4 || size.Height < 8)
		{
			return;
		}

		DrawGrid(context, size);
		if (_style is null)
		{
			return;
		}

		using (context.PushClip(new Rect(0, -2, size.Width, size.Height + 4)))
		using (context.PushTransform(Matrix.CreateTranslation(_shift, 0)))
		{
			// Markers first, so the traces stay on top; events newer than the edge scroll in with the wave.
			foreach (var (x, kind) in _markers)
			{
				context.DrawLine(MarkerPen(kind), new Point(x, _top), new Point(x, _bottom));
			}

			if (_coreLines is not null)
			{
				context.DrawGeometry(null, _style.CorePen, _coreLines);
			}

			if (_secondaryLine is not null)
			{
				context.DrawGeometry(null, _style.SecondaryPen, _secondaryLine);
			}

			if (_primaryFill is not null)
			{
				context.DrawGeometry(_fill, null, _primaryFill);
			}

			if (_primaryLine is not null)
			{
				context.DrawGeometry(null, _style.PrimaryPen, _primaryLine);
			}
		}

		DrawHead(context, _secondaryHead, _style.SecondaryBrush, _style.SecondaryHalo);
		DrawHead(context, _primaryHead, _style.PrimaryBrush, _style.PrimaryHalo);
	}

	protected override Size MeasureOverride(Size availableSize)
		=> new(0, double.IsInfinity(availableSize.Height) ? _desiredPlotHeight : Math.Min(_desiredPlotHeight, availableSize.Height));

	protected override Size ArrangeOverride(Size finalSize)
	{
		_fill = null;
		_dirty = true;
		return finalSize;
	}

	private static void DrawHead(DrawingContext context, Point? head, IBrush core, IBrush halo)
	{
		if (head is { } center)
		{
			context.DrawEllipse(halo, null, center, HaloRadius, HaloRadius);
			context.DrawEllipse(core, null, center, HeadRadius, HeadRadius);
		}
	}

	/// <summary>Where the trace meets the right edge, on screen; null when the trace does not reach it.</summary>
	private static Point? HeadOf(CurvePlanner curve, double shift, double width)
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
		return visible && screenX >= 0 ? new Point(screenX, y) : null;
	}

	private static StreamGeometry Build(CurvePlanner curve, bool filled, double baseline)
	{
		var geometry = new StreamGeometry();
		using (var context = geometry.Open())
		{
			var sink = new GeometrySink(context);
			curve.Emit(ref sink, filled, baseline);
			sink.Finish();
		}

		return geometry;
	}

	private static double SnapToPixelCenter(double value, double scale) => (Math.Floor(value * scale) + 0.5) / scale;

	private void Rebuild(double now, double width, double height)
	{
		var channel = _channel!;
		var style = _style!;
		_top = TopInset;
		_bottom = height - BottomInset;
		_pixelsPerSecond = width / _historySeconds;
		var map = new PlotMapping(now - _delaySeconds, width, _pixelsPerSecond, _top, _bottom, (_bottom - _top) / channel.Scale.Current);
		_fill ??= style.CreateFill(_top, _bottom);

		_markers.Clear();
		if (_showMarkers && _events is not null)
		{
			var title = ChannelText.TitleOf(channel);
			foreach (var item in _events.Between(map.TimeAt(0) - 5, double.PositiveInfinity))
			{
				if (item.BelongsTo(title))
				{
					_markers.Add((map.X(item.Time), item.Kind));
				}
			}
		}

		_coreLines = _showCores && channel.Cores.Length > 0 ? BuildCores(channel.Cores, map, _bottom) : null;
		if (channel.Secondary is not null)
		{
			_secondaryCurve.Plan(channel.Secondary, map, _gapSeconds);
			_secondaryLine = Build(_secondaryCurve, filled: false, _bottom);
		}
		else
		{
			_secondaryLine = null;
		}

		// The fill and the line are separate geometries: the fill closes down to the baseline, which is never stroked.
		_primaryCurve.Plan(channel.Primary, map, _gapSeconds);
		_primaryFill = Build(_primaryCurve, filled: true, _bottom);
		_primaryLine = Build(_primaryCurve, filled: false, _bottom);

		_builtAt = now;
		_builtVersion = channel.Version;
		_builtEvents = _events?.Version ?? -1;
		_dirty = false;
	}

	/// <summary>A dashed hairline in the colour of the event kind, like the markers in the terminal.</summary>
	private IPen MarkerPen(EventKind kind)
	{
		if (!_markerPens.TryGetValue(kind, out var pen))
		{
			pen = new ImmutablePen(new ImmutableSolidColorBrush(Palette.WithAlpha(Palette.MarkerOf(kind), 0.6)), 1, new ImmutableDashStyle([4, 2], 0));
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
			var sink = new GeometrySink(context);
			for (var i = 0; i < cores.Length; i++)
			{
				_coreCurves[i].Plan(cores[i], map, _gapSeconds);
				_coreCurves[i].Emit(ref sink, filled: false, baseline);
			}

			sink.Finish();
		}

		return geometry;
	}

	/// <summary>A fixed graticule of device-pixel hairlines: four vertical divisions and ten across.</summary>
	private void DrawGrid(DrawingContext context, Size size)
	{
		var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
		var top = TopInset;
		var bottom = size.Height - BottomInset;
		var hairline = 1 / scale;
		var division = hairline == 1 ? DivisionPen : new ImmutablePen(DivisionPen.Brush as IImmutableBrush, hairline);
		var line = hairline == 1 ? LinePen : new ImmutablePen(LinePen.Brush as IImmutableBrush, hairline);
		var baseline = hairline == 1 ? BaselinePen : new ImmutablePen(BaselinePen.Brush as IImmutableBrush, hairline);

		for (var i = 1; i < VerticalDivisions; i++)
		{
			var x = SnapToPixelCenter(size.Width * i / VerticalDivisions, scale);
			context.DrawLine(division, new Point(x, top), new Point(x, bottom));
		}

		for (var i = 0; i <= HorizontalDivisions; i++)
		{
			var y = SnapToPixelCenter(top + ((bottom - top) * i / HorizontalDivisions), scale);
			context.DrawLine(i == HorizontalDivisions ? baseline : line, new Point(0, y), new Point(size.Width, y));
		}
	}
}
