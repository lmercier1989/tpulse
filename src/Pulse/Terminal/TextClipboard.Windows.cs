namespace Pulse.Terminal;

/// <summary>Puts text on the clipboard.</summary>
internal static class TextClipboard
{
	/// <summary>Copies <paramref name="text"/>; false when the clipboard is held by another program.</summary>
	public static bool TrySetText(string text)
	{
		try
		{
			System.Windows.Clipboard.SetText(text);
			return true;
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
		{
			return false;
		}
	}
}
