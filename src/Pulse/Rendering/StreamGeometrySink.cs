using System.Windows;
using System.Windows.Media;

using Pulse.Core.Processing;

namespace Pulse.Rendering;

/// <summary>Writes a planned curve into a WPF <see cref="StreamGeometry"/>. A struct, so emitting does not box.</summary>
internal readonly struct StreamGeometrySink(StreamGeometryContext context) : IPathSink
{
	public void BeginFigure(double x, double y, bool filled) => context.BeginFigure(new Point(x, y), filled, isClosed: false);

	public void LineTo(double x, double y, bool stroked) => context.LineTo(new Point(x, y), stroked, isSmoothJoin: true);

	public void BezierTo(double x1, double y1, double x2, double y2, double x3, double y3)
		=> context.BezierTo(new Point(x1, y1), new Point(x2, y2), new Point(x3, y3), isStroked: true, isSmoothJoin: true);
}
