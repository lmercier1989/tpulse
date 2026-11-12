using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

using Pulse.Core.Processing;

namespace Pulse.Rendering;

/// <summary>The brushes and pens of one graph; immutable, so the render thread shares them without copies.</summary>
internal sealed class ScopeStyle
{
	private ScopeStyle(Color primary)
	{
		Primary = primary;
		Secondary = Palette.Tint(primary, 0.5);

		PrimaryBrush = new ImmutableSolidColorBrush(primary);
		SecondaryBrush = new ImmutableSolidColorBrush(Secondary);
		PrimaryPen = TracePen(PrimaryBrush, 1.6);
		SecondaryPen = TracePen(new ImmutableSolidColorBrush(Palette.WithAlpha(Secondary, 0.85)), 1.25);
		CorePen = TracePen(new ImmutableSolidColorBrush(Palette.WithAlpha(primary, 0.26)), 0.8);
		PrimaryHalo = new ImmutableSolidColorBrush(Palette.WithAlpha(primary, 0.18));
		SecondaryHalo = new ImmutableSolidColorBrush(Palette.WithAlpha(Secondary, 0.16));
	}

	public Color Primary { get; }

	public Color Secondary { get; }

	public IImmutableSolidColorBrush PrimaryBrush { get; }

	public IImmutableSolidColorBrush SecondaryBrush { get; }

	public IPen PrimaryPen { get; }

	public IPen SecondaryPen { get; }

	public IPen CorePen { get; }

	public IBrush PrimaryHalo { get; }

	public IBrush SecondaryHalo { get; }

	public static ScopeStyle For(MetricKind kind) => new(Palette.For(kind));

	/// <summary>The fill under the primary trace: a faint vertical gradient fixed to the plot, fading to nothing at zero.</summary>
	public IBrush CreateFill(double top, double bottom) => new ImmutableLinearGradientBrush(
		[
			new ImmutableGradientStop(0, Palette.WithAlpha(Primary, 0.20)),
			new ImmutableGradientStop(0.55, Palette.WithAlpha(Primary, 0.07)),
			new ImmutableGradientStop(1, Palette.WithAlpha(Primary, 0)),
		],
		startPoint: new RelativePoint(0, top, RelativeUnit.Absolute),
		endPoint: new RelativePoint(0, bottom, RelativeUnit.Absolute));

	private static ImmutablePen TracePen(IImmutableSolidColorBrush brush, double thickness)
		=> new(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
}
