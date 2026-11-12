using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Pulse.Core.Metrics;

/// <summary>
/// The marks file (%LOCALAPPDATA%\Pulse\marks.log): one line per marker put with <c>tpulse -mark</c> or by a command
/// measured with <c>tpulse -- command</c>. Every running Pulse reads the lines written after it started and draws them as
/// markers, so a script in one shell marks the graphs in another tab.
/// </summary>
public sealed class MarkLog
{
	/// <summary>Past this size the next mark starts the file over, so it never grows without bound.</summary>
	internal const long MaximumLength = 256 * 1024;

	private long _position;

	/// <param name="path">The marks file; only marks written after this moment are read.</param>
	public MarkLog(string path)
	{
		FilePath = path;
		_position = File.Exists(path) ? new FileInfo(path).Length : 0;
	}

	public string FilePath { get; }

	/// <summary>%LOCALAPPDATA%\Pulse\marks.log.</summary>
	public static string DefaultPath()
		=> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "marks.log");

	/// <summary>Appends one mark: its wall time, action ("mark", "run", "done", "failed"), source and text, tab-separated.</summary>
	public static void Append(string path, DateTimeOffset at, string action, string source, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var line = string.Join('\t', at.ToString("O", CultureInfo.InvariantCulture), Clean(action), Clean(source), Clean(text)) + "\n";
		var mode = File.Exists(path) && new FileInfo(path).Length > MaximumLength ? FileMode.Create : FileMode.Append;
		using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
		stream.Write(Encoding.UTF8.GetBytes(line));
	}

	/// <summary>
	/// Adds the marks written since the last call to <paramref name="into"/>, placed on the sample clock by their wall time:
	/// <paramref name="time"/> is now on the sample clock, <paramref name="wallNow"/> now on the wall clock.
	/// </summary>
	public void Drain(double time, DateTimeOffset wallNow, List<PulseEvent> into)
	{
		if (!File.Exists(FilePath))
		{
			_position = 0;
			return;
		}

		using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (stream.Length < _position)
		{
			_position = 0; // The file started over.
		}

		if (stream.Length == _position)
		{
			return;
		}

		stream.Position = _position;
		using var reader = new StreamReader(stream, Encoding.UTF8);
		var text = reader.ReadToEnd();
		var end = text.LastIndexOf('\n');
		if (end < 0)
		{
			return; // A line that is still being written.
		}

		_position += Encoding.UTF8.GetByteCount(text.AsSpan(0, end + 1));
		foreach (var line in text[..end].Split('\n'))
		{
			if (TryParse(line, time, wallNow, out var mark))
			{
				into.Add(mark);
			}
		}
	}

	/// <summary>One line of the file as an event, at its wall time seen from <paramref name="wallNow"/>.</summary>
	internal static bool TryParse(string line, double time, DateTimeOffset wallNow, [NotNullWhen(true)] out PulseEvent? mark)
	{
		mark = null;
		var parts = line.TrimEnd('\r').Split('\t');
		if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
		{
			return false;
		}

		var ago = Math.Max(0, (wallNow - at).TotalSeconds);
		mark = new PulseEvent(time - ago, at, EventKind.Mark, parts[2], parts[1], parts[3]);
		return true;
	}

	private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
