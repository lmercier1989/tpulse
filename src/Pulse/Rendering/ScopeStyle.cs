using System.Windows;
using System.Windows.Media;

using Pulse.Core.Processing;

namespace Pulse.Rendering;

/// <summary>The frozen brushes and pens of one graph. Frozen resources are shared with the render thread without copies.</summary>
internal sealed class ScopeStyle
{
	private ScopeStyle(Color primary)
	{
		Primary = primary;
		Secondary = Palette.Tint(primary, 0.5);

		PrimaryBrush = Freeze(new SolidColorBrush(primary));
		SecondaryBrush = Freeze(new SolidColorBrush(Secondary));
		PrimaryPen = Freeze(TracePen(PrimaryBrush, 1.6));
		SecondaryPen = Freeze(TracePen(Freeze(new SolidColorBrush(Palette.WithAlpha(Secondary, 0.85))), 1.25));
		CorePen = Freeze(TracePen(Freeze(new SolidColorBrush(Palette.WithAlpha(primary, 0.26))), 0.8));
		PrimaryHalo = Freeze(new SolidColorBrush(Palette.WithAlpha(primary, 0.18)));
		SecondaryHalo = Freeze(new SolidColorBrush(Palette.WithAlpha(Secondary, 0.16)));
	}

	public Color Primary { get; }

	public Color Secondary { get; }

	public SolidColorBrush PrimaryBrush { get; }

	public SolidColorBrush SecondaryBrush { get; }

	public Pen PrimaryPen { get; }

	public Pen SecondaryPen { get; }

	public Pen CorePen { get; }

	public Brush PrimaryHalo { get; }

	public Brush SecondaryHalo { get; }

	public static ScopeStyle For(MetricKind kind) => new(Palette.For(kind));

	/// <summary>The fill under the primary trace: a faint vertical gradient fixed to the plot, fading to nothing at zero.</summary>
	public Brush CreateFill(double top, double bottom) => Freeze(new LinearGradientBrush
	{
		MappingMode = BrushMappingMode.Absolute,
		StartPoint = new Point(0, top),
		EndPoint = new Point(0, bottom),
		GradientStops =
		{
			new GradientStop(Palette.WithAlpha(Primary, 0.20), 0),
			new GradientStop(Palette.WithAlpha(Primary, 0.07), 0.55),
			new GradientStop(Palette.WithAlpha(Primary, 0), 1),
		},
	});

	private static Pen TracePen(Brush brush, double thickness) => new(brush, thickness)
	{
		LineJoin = PenLineJoin.Round,
		StartLineCap = PenLineCap.Round,
		EndLineCap = PenLineCap.Round,
	};

	private static T Freeze<T>(T freezable)
		where T : Freezable
	{
		freezable.Freeze();
		return freezable;
	}
}
