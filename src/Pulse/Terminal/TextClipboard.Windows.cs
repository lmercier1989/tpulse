namespace Pulse.Terminal;

/// <summary>Puts text on the clipboard.</summary>
internal static class TextClipboard
{
	/// <summary>Copies <paramref name="text"/>; false when the clipboard is held by another program.</summary>
	public static bool TrySetText(string text)
	{
		try
		{
			// The terminal loop runs on the main thread, a single-threaded apartment ([STAThread] on Main).
			System.Windows.Forms.Clipboard.SetText(text);
			return true;
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
		{
			return false;
		}
	}
}
