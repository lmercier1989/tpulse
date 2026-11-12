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
	/// <summary>
	/// Runs <paramref name="command"/> <paramref name="repeat"/> times, stopping at the first that fails, and returns the
	/// exit code of the last run; 127 when it cannot be started. After several runs their average, fastest and slowest
	/// follow.
	/// </summary>
	public static int Run(IReadOnlyList<string> command, int repeat = 1)
	{
		var line = string.Join(' ', command.Select(Quote));

		// Ctrl+C reaches the command too; tpulse waits for it to stop and still reports.
		Console.CancelKeyPress += (_, e) => e.Cancel = true;
		var durations = new List<double>();
		var exitCode = 0;
		for (var round = 1; round <= repeat; round++)
		{
			if (repeat > 1)
			{
				Console.Error.WriteLine($"tpulse: run {round} of {repeat}");
			}

			exitCode = RunOnce(command, line, out var seconds);
			if (seconds is not { } taken)
			{
				return exitCode;
			}

			durations.Add(taken);
			if (exitCode != 0)
			{
				break;
			}

			if (round < repeat)
			{
				Console.Error.WriteLine();
			}
		}

		if (durations.Count > 1)
		{
			Console.Error.WriteLine();
			Console.Error.WriteLine(RunHistory.Repeated(line, durations));
		}

		return exitCode;
	}

	/// <summary>
	/// One run: marks its start and end, samples the machine every second and follows the processes it starts, then prints
	/// what it cost, how it compares with the runs of the same command here before, and what it left running. The run is
	/// added to the history. <paramref name="seconds"/> is null when the command could not be started.
	/// </summary>
	private static int RunOnce(IReadOnlyList<string> command, string line, out double? seconds)
	{
		seconds = null;
		var source = Source();
		var folder = Environment.CurrentDirectory;
		var started = TimeProvider.System.GetUtcNow();
		Mark("run", source, line);

		var summary = new RunSummary();
		var tree = new CommandTree();
		using var collector = SystemMetricsCollector.Create();
		using var sampler = new Sampler(collector, TimeSpan.FromSeconds(1), sample =>
		{
			lock (summary)
			{
				summary.Add(sample);
			}

			tree.Sample();
		});
		sampler.Start();

		var watch = Stopwatch.StartNew();
		int exitCode;
		try
		{
			using var process = Process.Start(StartInfo(command)) ?? throw new Win32Exception("The process did not start.");
			tree.Start(process.Id);
			process.WaitForExit();
			exitCode = process.ExitCode;
		}
		catch (Win32Exception ex)
		{
			Console.Error.WriteLine($"tpulse: {command[0]} could not be started: {ex.Message}");
			Mark("failed", source, $"{line} · could not be started");
			return 127;
		}

		var taken = Math.Max(0.1, watch.Elapsed.TotalSeconds);
		seconds = taken;

		// A process that is ending as the command ends is given a moment, so only what stays is named.
		var left = tree.Running();
		if (left.Count > 0)
		{
			Thread.Sleep(500);
			left = tree.Running();
		}

		List<string> lines;
		RunRecord record;
		lock (summary)
		{
			lines = summary.Lines(line, taken, exitCode);
			record = summary.Record(started, folder, line, taken, exitCode, RunHistory.CommitIn(folder));
		}

		var history = RunHistory.DefaultPath();
		try
		{
			if (RunHistory.Compare(RunHistory.Load(history), record) is { } compared)
			{
				lines.Add(compared);
			}

			RunHistory.Append(history, record);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"tpulse: the run history could not be updated: {ex.Message}");
		}

		if (RunSummary.LeftRunning(left) is { } running)
		{
			lines.Add(running);
		}

		Console.Error.WriteLine();
		foreach (var text in lines)
		{
			Console.Error.WriteLine(text);
		}

		Mark(exitCode == 0 ? "done" : "failed", source, $"{line} · {Core.Formatting.Units.Duration(taken)} · exit code {exitCode}");
		return exitCode;
	}

	/// <summary><c>tpulse -runs</c>: the commands measured in this folder (or, with <paramref name="everywhere"/>, in every folder), latest first.</summary>
	public static int ListRuns(bool everywhere)
	{
		const int Count = 20;
		var path = RunHistory.DefaultPath();
		List<RunRecord> runs;
		try
		{
			runs = RunHistory.Load(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"tpulse: the run history cannot be read: {ex.Message}");
			return 1;
		}

		var folder = everywhere ? null : Environment.CurrentDirectory;
		var lines = RunHistory.List(runs, folder, Count);
		if (lines.Count == 0)
		{
			Console.WriteLine(everywhere || runs.Count == 0
				? "No command has been measured yet. Measure one with: tpulse -- dotnet build"
				: $"No command has been measured in {folder} yet; tpulse -runs all lists every folder.");
			return 0;
		}

		Console.WriteLine(everywhere ? "Commands measured with tpulse --, latest first:" : $"Commands measured in {folder}, latest first:");
		Console.WriteLine();
		foreach (var text in lines)
		{
			Console.WriteLine(text);
		}

		Console.WriteLine();
		Console.WriteLine($"Kept in {path}.{(everywhere ? string.Empty : " tpulse -runs all lists every folder.")}");
		return 0;
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
