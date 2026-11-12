using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using Pulse.Core.Metrics;
using Pulse.Core.Presentation;

namespace Pulse;

/// <summary>
/// <c>tpulse -- command</c>: runs the command in this console, as if it was typed without tpulse, while sampling the
/// machine every second; puts a mark on every running Pulse when it starts and when it ends, and prints what it cost.
/// <c>tpulse -mark text</c> puts a mark of its own.
/// </summary>
internal static class CommandRunner
{
	/// <summary>Runs <paramref name="command"/> and returns its exit code; 127 when it cannot be started.</summary>
	public static int Run(IReadOnlyList<string> command)
	{
		var line = string.Join(' ', command.Select(Quote));
		var source = Source();
		Mark("run", source, line);

		var summary = new RunSummary();
		using var collector = SystemMetricsCollector.Create();
		using var sampler = new Sampler(collector, TimeSpan.FromSeconds(1), sample =>
		{
			lock (summary)
			{
				summary.Add(sample);
			}
		});
		sampler.Start();

		// Ctrl+C reaches the command too; tpulse waits for it to stop and still reports.
		Console.CancelKeyPress += (_, e) => e.Cancel = true;
		var watch = Stopwatch.StartNew();
		int exitCode;
		try
		{
			using var process = Process.Start(StartInfo(command)) ?? throw new Win32Exception("The process did not start.");
			process.WaitForExit();
			exitCode = process.ExitCode;
		}
		catch (Win32Exception ex)
		{
			Console.Error.WriteLine($"tpulse: {command[0]} could not be started: {ex.Message}");
			Mark("failed", source, $"{line} · could not be started");
			return 127;
		}

		var seconds = Math.Max(0.1, watch.Elapsed.TotalSeconds);
		List<string> lines;
		lock (summary)
		{
			lines = summary.Lines(line, seconds, exitCode);
		}

		Console.Error.WriteLine();
		foreach (var text in lines)
		{
			Console.Error.WriteLine(text);
		}

		Mark(exitCode == 0 ? "done" : "failed", source, $"{line} · {Core.Formatting.Units.Duration(seconds)} · exit code {exitCode}");
		return exitCode;
	}

	/// <summary>Puts a mark on every running Pulse; a mark that cannot be written is reported, not fatal.</summary>
	public static void Mark(string action, string source, string text)
	{
		try
		{
			MarkLog.Append(MarkLog.DefaultPath(), TimeProvider.System.GetLocalNow(), action, source, text);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"tpulse: the mark could not be written: {ex.Message}");
		}
	}

	/// <summary>Who puts a mark: the folder the shell is in, such as the repository being built.</summary>
	public static string Source() => Path.GetFileName(Environment.CurrentDirectory.TrimEnd('\\', '/')) is { Length: > 0 } folder ? folder : "tpulse";

	/// <summary>
	/// A program (.exe) runs directly with its arguments; anything else (npm.cmd, a .bat, a shell built-in) runs through
	/// cmd, which finds and runs it the way the shell does. On Linux a program on PATH runs directly, anything else (a
	/// shell built-in or function) through /bin/sh.
	/// </summary>
	private static ProcessStartInfo StartInfo(IReadOnlyList<string> command)
	{
		if (!OperatingSystem.IsWindows())
		{
			var found = Resolve(command[0]);
			var start = new ProcessStartInfo(found ?? "/bin/sh") { UseShellExecute = false };
			if (found is null)
			{
				start.ArgumentList.Add("-c");
				start.ArgumentList.Add(string.Join(' ', command.Select(ShellQuote)));
			}
			else
			{
				foreach (var argument in command.Skip(1))
				{
					start.ArgumentList.Add(argument);
				}
			}

			return start;
		}

		if (Resolve(command[0]) is { } program && Path.GetExtension(program).ToLowerInvariant() is ".exe" or ".com")
		{
			var direct = new ProcessStartInfo(program) { UseShellExecute = false };
			foreach (var argument in command.Skip(1))
			{
				direct.ArgumentList.Add(argument);
			}

			return direct;
		}

		return new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
		{
			UseShellExecute = false,
			Arguments = $"/d /s /c \"{string.Join(' ', command.Select(Quote))}\"",
		};
	}

	/// <summary>The file <paramref name="name"/> stands for: a path as it is, else the first match on PATH with an extension of PATHEXT.</summary>
	private static string? Resolve(string name)
	{
#if !WINDOWS
		if (!OperatingSystem.IsWindows())
		{
			IEnumerable<string> places = name.Contains('/')
				? [name]
				: (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries).Select(folder => Path.Combine(folder, name));
			return places.FirstOrDefault(path => File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) is { } found
				? Path.GetFullPath(found)
				: null;
		}
#endif

		var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
		IEnumerable<string> folders = name.Contains('\\') || name.Contains('/')
			? [string.Empty]
			: [Environment.CurrentDirectory, .. (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)];
		foreach (var folder in folders)
		{
			var path = folder.Length == 0 ? name : Path.Combine(folder.Trim('"'), name);
			if (Path.HasExtension(path) && File.Exists(path))
			{
				return Path.GetFullPath(path);
			}

			foreach (var extension in extensions)
			{
				if (File.Exists(path + extension))
				{
					return Path.GetFullPath(path + extension);
				}
			}
		}

		return null;
	}

	/// <summary>An argument for /bin/sh: in single quotes unless it is plain.</summary>
	private static string ShellQuote(string argument)
		=> argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,+@%".Contains(c, StringComparison.Ordinal))
			? argument
			: $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'";

	private static string Quote(string argument) => argument.Length == 0 || argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
}
