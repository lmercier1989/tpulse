using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pulse.Core.Diagnostics;

/// <summary>
/// A small diagnostics log in <c>%LOCALAPPDATA%\Pulse\logs\pulse.log</c>. It rolls over to
/// <c>pulse.1.log</c> at 1 MB, so it never grows without bound. Thread-safe.
/// Before <see cref="Initialize"/> (and in tests) messages go to the debugger output only.
/// </summary>
public static class Log
{
	private const long MaxBytes = 1024 * 1024;

	private static readonly Lock Gate = new();
	private static readonly HashSet<string> Reported = [];
	private static StreamWriter? _writer;
	private static string? _path;

	/// <summary>The log file, once initialized.</summary>
	public static string? FilePath => _path;

	public static void Initialize(string directory)
	{
		lock (Gate)
		{
			try
			{
				Directory.CreateDirectory(directory);
				_path = Path.Combine(directory, "pulse.log");
				OpenWriter();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Debug.WriteLine($"Pulse log unavailable: {ex.Message}");
				_writer = null;
			}
		}
	}

	public static void Info(string message) => Write("INFO", message, null);

	public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

	public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

	/// <summary>Logs a warning the first time <paramref name="key"/> is seen; later repeats are dropped.</summary>
	public static void WarnOnce(string key, string message, Exception? exception = null)
	{
		lock (Gate)
		{
			if (!Reported.Add(key))
			{
				return;
			}
		}

		Write("WARN", message, exception);
	}

	public static void Shutdown()
	{
		lock (Gate)
		{
			_writer?.Dispose();
			_writer = null;
		}
	}

	private static void Write(string level, string message, Exception? exception)
	{
		var line = new StringBuilder(128)
			.Append(TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
			.Append(' ')
			.Append(level.PadRight(5))
			.Append(" [")
			.Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture).PadLeft(2))
			.Append("] ")
			.Append(message);
		if (exception is not null)
		{
			line.AppendLine().Append(exception);
		}

		var text = line.ToString();
		Debug.WriteLine(text);

		lock (Gate)
		{
			if (_writer is null)
			{
				return;
			}

			try
			{
				_writer.WriteLine(text);
				if (_writer.BaseStream.Length > MaxBytes)
				{
					Roll();
				}
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				Debug.WriteLine($"Pulse log write failed: {ex.Message}");
			}
		}
	}

	private static void OpenWriter()
	{
		if (_path is null)
		{
			return;
		}

		if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
		{
			File.Move(_path, RolledPath(_path), overwrite: true);
		}

		var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
		_writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
	}

	private static void Roll()
	{
		_writer?.Dispose();
		_writer = null;
		OpenWriter();
	}

	private static string RolledPath(string path) => Path.ChangeExtension(path, ".1.log");
}
