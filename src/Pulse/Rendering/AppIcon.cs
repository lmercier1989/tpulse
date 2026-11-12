using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace Pulse.Rendering;

/// <summary>Draws the window and taskbar icon: a cyan pulse on the instrument background.</summary>
internal static class AppIcon
{
	public static WindowIcon Create()
	{
		const int size = 64;
		var trace = new StreamGeometry();
		using (var context = trace.Open())
		{
			context.BeginFigure(new Point(7, 36), false);
			foreach (var point in new Point[] { new(20, 36), new(26, 21), new(33, 48), new(40, 14), new(46, 36), new(57, 36) })
			{
				context.LineTo(point, true);
			}

			context.EndFigure(false);
		}

		var pen = new ImmutablePen(new ImmutableSolidColorBrush(Palette.Cpu), 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
		var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
		using (var drawing = bitmap.CreateDrawingContext())
		{
			drawing.DrawRectangle(new ImmutableSolidColorBrush(Palette.Background), null, new RoundedRect(new Rect(0, 0, size, size), 14));
			drawing.DrawGeometry(null, pen, trace);
		}

		return new WindowIcon(bitmap);
	}
}
