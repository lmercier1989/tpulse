using System.Diagnostics;
using System.IO;

using Pulse.Core.CommandLine;
using Pulse.Core.Diagnostics;
using Pulse.Core.Metrics;
using Pulse.Core.Settings;
using Pulse.Terminal;

namespace Pulse;

/// <summary>
/// The tpulse command. By default the monitor draws inside the terminal it was started from; with
/// <c>-window</c> it starts itself again, detached, as a graphical window and returns the prompt at once.
/// Profiles are expanded first, so every later step sees the options they stand for.
/// </summary>
internal static class Program
{
	[STAThread]
	private static int Main(string[] commandLine)
	{
		// tpulse -- dotnet build: measure a command; tpulse -mark text: put a mark on every running Pulse.
		if (Array.IndexOf(commandLine, "--") is var separator and >= 0)
		{
			if (separator == commandLine.Length - 1)
			{
				Console.Error.WriteLine("Put a command after --, for example: tpulse -- dotnet build");
				return 2;
			}

			return CommandRunner.Run(commandLine[(separator + 1)..]);
		}

		if (Array.FindIndex(commandLine, arg => Profiles.IsOption(arg, "mark", out _)) is var markAt and >= 0)
		{
			Profiles.IsOption(commandLine[markAt], "mark", out var inline);
			var text = string.Join(' ', [.. inline is null ? [] : new[] { inline }, .. commandLine[(markAt + 1)..]]).Trim();
			if (text.Length == 0)
			{
				Console.Error.WriteLine("Put the text of the mark after -mark, for example: tpulse -mark deploy started");
				return 2;
			}

			CommandRunner.Mark("mark", CommandRunner.Source(), text);
			return 0;
		}

		var profiles = new Profiles(Profiles.DefaultPath());
		if (commandLine.Any(arg => Profiles.IsOption(arg, "profiles", out _)))
		{
			return ListProfiles(profiles);
		}

		if (Array.FindIndex(commandLine, arg => Profiles.IsOption(arg, "save-profile", out _)) is var saveAt and >= 0)
		{
			return SaveProfile(profiles, commandLine, saveAt);
		}

		List<string> expanded;
		try
		{
			if (!Profiles.TryExpand(commandLine, profiles.Load(), out expanded, out var profileError))
			{
				Console.Error.WriteLine(profileError);
				return 2;
			}
		}
		catch (InvalidDataException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 2;
		}

		string[] args = [.. expanded];
		if (!LaunchOptions.TryParse(args, out var options, out var error))
		{
			Console.Error.WriteLine(error);
			Console.Error.WriteLine();
			Console.Error.WriteLine(LaunchOptions.HelpText);
			return 2;
		}

		if (options.ShowHelp || options.ShowVersion)
		{
			Console.WriteLine(options.ShowHelp ? LaunchOptions.HelpText : $"Pulse {AppInfo.Version}");
			return 0;
		}

		Recording? recording = null;
		if (options.ReplayPath is { } replay)
		{
			if (options.Window)
			{
				Console.Error.WriteLine("A recording plays in the terminal; leave out -window.");
				return 2;
			}

			try
			{
				recording = Recording.Load(replay);
			}
			catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
			{
				Console.Error.WriteLine($"The recording cannot be played: {ex.Message}");
				return 1;
			}
		}

		// A machine that cannot be read, a counter that does not exist or a folder without a repository is reported here in
		// the shell, before a tab or a window opens and closes again.
		if (!options.WindowHost && recording is null && (options.Host is not null || options.Watches.Count > 0 || options.GitPaths.Count > 0))
		{
			if (options.Host is not null)
			{
				Console.WriteLine($"Connecting to {options.Host}...");
			}

			if (SystemMetricsCollector.Check(options.Host, options.Watches, options.GitPaths) is { } problem)
			{
				Console.Error.WriteLine(problem);
				return 1;
			}
		}

		if (options.Window && !options.WindowHost)
		{
			return StartDetachedWindow(args);
		}

		// In Windows Terminal, Pulse gets a tab of its own rather than taking over the shell's tab. The recording file is
		// chosen here and named in this shell, because the Pulse tab closes when Pulse quits.
		var record = options.RecordPath == string.Empty && recording is null;
		var tabArguments = record ? [.. args, "-record", CsvRecorder.DefaultPath(options.Host)] : args;
		if (!options.Window && !options.Here && PulseTab.TryOpen(tabArguments))
		{
			if (options.RecordPath is not null && recording is null)
			{
				Console.WriteLine($"Pulse opened in its own tab, recording to {(options.RecordPath.Length > 0 ? Path.GetFullPath(options.RecordPath) : tabArguments[^1])}");
			}

			return 0;
		}

		var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse");
		Log.Initialize(Path.Combine(dataDirectory, "logs"));
		Log.Info($"Pulse {AppInfo.Version} starting ({(options.WindowHost ? "window" : "terminal")}); arguments: [{string.Join(' ', args)}]; {Environment.OSVersion}; .NET {Environment.Version}");
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception.", e.ExceptionObject as Exception);
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Log.Error("Unobserved task exception.", e.Exception);
			e.SetObserved();
		};

		var store = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
		var saved = store.Load();
		var session = saved.Clone();
		options.ApplyTo(session);

		try
		{
			// git: the folders of -git; without them every repository Pulse finds on this machine, also next to -host only
			// when -git asks for it.
			IReadOnlyList<string>? gitPaths = options.GitPaths.Count > 0 ? options.GitPaths
				: options.Host is null || options.Metrics?.Contains(Core.Processing.MetricKind.Git) == true ? []
				: null;
			using var collector = recording is null ? SystemMetricsCollector.Create(options.Host, options.Watches, gitPaths) : null;
			if (options.WindowHost)
			{
				var app = new App(collector!, store, saved, session, options.AffectsLayout);
				app.InitializeComponent();
				return app.Run();
			}

			using var host = ConsoleHost.TryOpen();
			if (host is null)
			{
				Console.Error.WriteLine("tpulse draws in a terminal, but its input or output is redirected. Run it in a terminal, or use tpulse -window.");
				return 1;
			}

			var terminal = new TerminalApp(collector, recording, saved, session, options);
			terminal.Run(host);
			store.Save(saved);
			if (terminal.RecordedFile is { } recorded)
			{
				Console.WriteLine($"Samples recorded to {recorded}");
			}

			return 0;
		}
		catch (Exception ex)
		{
			Log.Error("Pulse stopped on an error.", ex);
			Console.Error.WriteLine($"tpulse stopped: {ex.Message}");
			Console.Error.WriteLine($"Details: {Log.FilePath}");
			return 1;
		}
		finally
		{
			Log.Info("Pulse exited.");
			Log.Shutdown();
		}
	}

	/// <summary><c>tpulse -profiles</c>: every profile with the options it stands for.</summary>
	private static int ListProfiles(Profiles profiles)
	{
		try
		{
			var all = profiles.Load();
			var width = all.Keys.Max(name => name.Length);
			foreach (var (name, options) in all.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
			{
				Console.WriteLine($"  {name.PadRight(width)}   {string.Join(' ', options.Select(Quote))}");
			}

			Console.WriteLine();
			Console.WriteLine($"Use one with tpulse -profile <name>; they are kept in {profiles.FilePath}.");
			return 0;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
	}

	/// <summary><c>tpulse -save-profile name options...</c>: checks the options, then saves them under the name.</summary>
	private static int SaveProfile(Profiles profiles, string[] commandLine, int at)
	{
		Profiles.IsOption(commandLine[at], "save-profile", out var inline);
		var name = inline ?? (at + 1 < commandLine.Length ? commandLine[at + 1] : string.Empty);
		var rest = commandLine.Where((_, index) => index != at && (inline is not null || index != at + 1)).ToList();
		if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
		{
			Console.Error.WriteLine("Option -save-profile needs a name, for example tpulse -save-profile dev -cpu -git.");
			return 2;
		}

		if (!LaunchOptions.TryParse(rest, out _, out var error))
		{
			Console.Error.WriteLine(error);
			return 2;
		}

		try
		{
			profiles.Save(name, rest);
			Console.WriteLine($"Saved profile {name}: {string.Join(' ', rest.Select(Quote))}");
			return 0;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
	}

	private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

	/// <summary>Starts this executable again as a window without a console, and returns at once.</summary>
	private static int StartDetachedWindow(string[] args)
	{
		var start = new ProcessStartInfo(Environment.ProcessPath ?? "tpulse.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Environment.CurrentDirectory,
		};

		foreach (var argument in args)
		{
			if (argument.TrimStart('-', '/').ToLowerInvariant() is not ("window" or "gui"))
			{
				start.ArgumentList.Add(argument);
			}
		}

		start.ArgumentList.Add("--window-host");
		using var process = Process.Start(start);
		return process is null ? 1 : 0;
	}
}
