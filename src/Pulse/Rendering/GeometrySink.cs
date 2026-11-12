using Avalonia;
using Avalonia.Media;

using Pulse.Core.Processing;

namespace Pulse.Rendering;

/// <summary>
/// Writes a planned curve into an Avalonia <see cref="StreamGeometryContext"/>. A struct, so emitting does not box;
/// Avalonia needs every figure ended, so call <see cref="Finish"/> after the last one.
/// </summary>
internal struct GeometrySink(StreamGeometryContext context) : IPathSink
{
	private bool _open;

	public void BeginFigure(double x, double y, bool filled)
	{
		Finish();
		context.BeginFigure(new Point(x, y), filled);
		_open = true;
	}

	public readonly void LineTo(double x, double y, bool stroked) => context.LineTo(new Point(x, y), stroked);

	public readonly void BezierTo(double x1, double y1, double x2, double y2, double x3, double y3)
		=> context.CubicBezierTo(new Point(x1, y1), new Point(x2, y2), new Point(x3, y3), true);

	/// <summary>Ends the open figure, left open: a filled figure is closed by its fill, never by a stroke.</summary>
	public void Finish()
	{
		if (_open)
		{
			context.EndFigure(false);
			_open = false;
		}
	}
}
