using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pulse.Rendering;

/// <summary>Draws the window and taskbar icon: a cyan pulse on the instrument background.</summary>
internal static class AppIcon
{
	public static ImageSource Create()
	{
		const int size = 64;
		var trace = new StreamGeometry();
		using (var context = trace.Open())
		{
			context.BeginFigure(new Point(7, 36), isFilled: false, isClosed: false);
			context.PolyLineTo(
				[new Point(20, 36), new Point(26, 21), new Point(33, 48), new Point(40, 14), new Point(46, 36), new Point(57, 36)],
				isStroked: true,
				isSmoothJoin: true);
		}

		var pen = new Pen(new SolidColorBrush(Palette.Cpu), 5)
		{
			LineJoin = PenLineJoin.Round,
			StartLineCap = PenLineCap.Round,
			EndLineCap = PenLineCap.Round,
		};

		var visual = new DrawingVisual();
		using (var drawing = visual.RenderOpen())
		{
			drawing.DrawRoundedRectangle(new SolidColorBrush(Palette.Background), null, new Rect(0, 0, size, size), 14, 14);
			drawing.DrawGeometry(null, pen, trace);
		}

		var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		bitmap.Freeze();
		return bitmap;
	}
}
