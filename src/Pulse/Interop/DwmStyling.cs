using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Pulse.Rendering;

namespace Pulse.Interop;

/// <summary>
/// Keeps the native title bar (snap layouts, system menu, accessibility) but paints it dark to match the
/// window. Dark mode works from Windows 10 20H1; the caption, text and border colours from Windows 11.
/// Older systems ignore the attributes they do not know.
/// </summary>
internal static partial class DwmStyling
{
	private const int UseImmersiveDarkMode = 20;
	private const int BorderColor = 34;
	private const int CaptionColor = 35;
	private const int TextColor = 36;

	public static void Apply(Window window)
	{
		var handle = new WindowInteropHelper(window).Handle;
		if (handle == 0)
		{
			return;
		}

		var enabled = 1;
		_ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
		SetColor(handle, CaptionColor, Palette.Background);
		SetColor(handle, TextColor, Palette.TitleText);
		SetColor(handle, BorderColor, Palette.WindowBorder);
	}

	private static void SetColor(nint handle, int attribute, Color color)
	{
		var colorRef = color.R | (color.G << 8) | (color.B << 16);
		_ = DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
	}

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
