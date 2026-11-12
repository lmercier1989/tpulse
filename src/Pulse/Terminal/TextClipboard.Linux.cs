using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Pulse.Terminal;

/// <summary>
/// Puts text on the clipboard on Linux: through wl-copy on Wayland, xclip or xsel on X11, and otherwise by asking the
/// terminal itself (OSC 52), which also works over SSH in the terminals that allow it.
/// </summary>
internal static class TextClipboard
{
	/// <summary>Copies <paramref name="text"/>; false when no way to reach a clipboard worked.</summary>
	public static bool TrySetText(string text)
	{
		var tools = new List<(string Program, string[] Arguments)>();
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
		{
			tools.Add(("wl-copy", []));
		}

		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
		{
			tools.Add(("xclip", ["-selection", "clipboard"]));
			tools.Add(("xsel", ["--clipboard", "--input"]));
		}

		foreach (var (program, arguments) in tools)
		{
			if (TryPipe(program, arguments, text))
			{
				return true;
			}
		}

		try
		{
			using var stdout = Console.OpenStandardOutput();
			stdout.Write(Encoding.ASCII.GetBytes($"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\u0007"));
			stdout.Flush();
			return true;
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static bool TryPipe(string program, string[] arguments, string text)
	{
		var start = new ProcessStartInfo(program)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(start);
			if (process is null)
			{
				return false;
			}

			process.StandardInput.Write(text);
			process.StandardInput.Close();
			// wl-copy and xclip stay behind to serve the clipboard; they detach from the pipes first.
			return !process.WaitForExit(2000) || process.ExitCode == 0;
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
		{
			return false;
		}
	}
}
