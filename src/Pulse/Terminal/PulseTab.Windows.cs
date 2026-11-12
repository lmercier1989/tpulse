using System.Diagnostics;
using System.IO;

using Pulse.Core.Diagnostics;

namespace Pulse.Terminal;

/// <summary>
/// Typed in a shell inside Windows Terminal (PowerShell, cmd, ...), tpulse does not draw in that shell's tab:
/// it opens its own tab with the Pulse profile (instrument colours, font, icon) in the same window and returns
/// the prompt at once. It draws in place when it already is the Pulse tab, when it runs outside Windows
/// Terminal, when the Pulse profile is not installed, or when asked to with <c>-here</c>.
/// </summary>
internal static class PulseTab
{
	/// <summary>The profile id written by install.ps1; keep both the same.</summary>
	public const string ProfileGuid = "{6a3c58d1-0f2e-4b7c-9d1a-5e8f2c4b7a90}";

	/// <summary>Opens Pulse in its own tab; false when tpulse should draw where it is.</summary>
	public static bool TryOpen(IReadOnlyList<string> args)
	{
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
			|| string.Equals(Environment.GetEnvironmentVariable("WT_PROFILE_ID"), ProfileGuid, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (!File.Exists(Path.Combine(localData, @"Microsoft\Windows Terminal\Fragments\Pulse\pulse.json")))
		{
			return false;
		}

		var terminal = Path.Combine(localData, @"Microsoft\WindowsApps\wt.exe");
		var start = new ProcessStartInfo(File.Exists(terminal) ? terminal : "wt.exe") { UseShellExecute = false };
		// The tab starts in the folder of this shell, so relative paths (-git, -record, -replay) mean the same there.
		foreach (var argument in new[] { "-w", "0", "new-tab", "-p", "Pulse", "-d", Environment.CurrentDirectory, "--", Environment.ProcessPath ?? "tpulse.exe" })
		{
			start.ArgumentList.Add(argument);
		}

		foreach (var argument in args)
		{
			start.ArgumentList.Add(argument);
		}

		start.ArgumentList.Add("-here");
		try
		{
			using var process = Process.Start(start);
			return process is not null;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			Log.Warn("Could not open a Pulse tab in Windows Terminal; drawing in this tab instead.", ex);
			return false;
		}
	}
}
