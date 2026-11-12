using System.IO.Enumeration;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace Pulse.Core.Interop;

/// <summary>
/// All processes of the machine from <c>/proc</c>: id, parent, start time, CPU time, private memory, threads and the bytes
/// read and written, the same record the Windows snapshot gives. Times are converted to the 100 ns units of Windows, a
/// start time to a FILETIME, so everything that works on the records works unchanged. Names are cached per process, and
/// a file a process does not let Pulse read (the I/O of another user's process) is not tried again.
/// </summary>
internal sealed class ProcessSnapshot
{
	/// <summary>sysconf(_SC_CLK_TCK): 100 on every architecture .NET runs on with Linux.</summary>
	private const long ClockTicksPerSecond = 100;

	private const long HundredNanosecondsPerTick = 10_000_000 / ClockTicksPerSecond;

	private static readonly long PageSize = Environment.SystemPageSize;

	private readonly byte[] _buffer = new byte[4096];
	private readonly long _bootFileTime = ReadBootFileTime();
	private ProcessRecord[] _records = new ProcessRecord[1024];
	private char[] _names = new char[32 * 1024];
	private int _namesUsed;
	private Dictionary<(int Id, long Created), Known> _known = [];
	private Dictionary<(int Id, long Created), Known> _seen = [];

	public static bool IsSupported => Directory.Exists("/proc/self");

	/// <summary>Program names to mark in <see cref="ProcessRecord.Watch"/>, such as "code"; compared ignoring case.</summary>
	public string[] WatchedNames { get; set; } = [];

	/// <summary>Process ids watched by id; their open files are counted like those of a watched name.</summary>
	public int[] WatchedIds { get; init; } = [];

	/// <summary>Takes a snapshot; empty when <c>/proc</c> cannot be read. Valid until the next call.</summary>
	public ReadOnlySpan<ProcessRecord> Take()
	{
		var count = 0;
		_namesUsed = 0;
		_seen.Clear();
		IEnumerable<int> ids;
		try
		{
			ids = new FileSystemEnumerable<int>("/proc", (ref entry) => int.Parse(entry.FileName, System.Globalization.CultureInfo.InvariantCulture))
			{
				ShouldIncludePredicate = (ref entry) => entry.IsDirectory && IsNumber(entry.FileName),
			};
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return default;
		}

		foreach (var id in ids)
		{
			var folder = $"/proc/{id}/";
			var read = Read(folder + "stat");
			if (read <= 0 || !LinuxProc.TryParseStat(_buffer.AsSpan(0, read), out var stat, out var command))
			{
				continue; // Ended since the folder was listed.
			}

			var created = _bootFileTime + (stat.StartTicks * HundredNanosecondsPerTick);
			var key = (id, created);
			if (!_known.TryGetValue(key, out var known))
			{
				known = new Known(NameOf(folder, command), CanReadIo: true);
			}

			if (count == _records.Length)
			{
				Array.Resize(ref _records, _records.Length * 2);
			}

			ref var record = ref _records[count++];
			record.Id = id;
			record.ParentId = stat.ParentId;
			record.CreateTime = created;
			record.CpuTime = (stat.UserTicks + stat.SystemTicks) * HundredNanosecondsPerTick;
			record.Threads = stat.Threads;
			read = Read(folder + "statm");
			record.PrivateBytes = read > 0 ? LinuxProc.PrivatePages(_buffer.AsSpan(0, read)) * PageSize : 0;
			record.IoBytes = 0;
			if (known.CanReadIo)
			{
				read = Read(folder + "io");
				if (read > 0)
				{
					record.IoBytes = LinuxProc.IoBytes(_buffer.AsSpan(0, read));
				}
				else
				{
					known = known with { CanReadIo = false };
				}
			}

			var name = known.Name.AsSpan();
			record.NameOffset = Store(name);
			record.NameLength = name.Length;
			record.IsClaude = name.SequenceEqual("claude") || name.SequenceEqual("claude-desktop");
			record.Watch = IndexOfWatched(name);
			record.Handles = record.Watch >= 0 || Array.IndexOf(WatchedIds, id) >= 0 ? CountOpenFiles(folder) : 0;
			_seen[key] = known;
		}

		(_known, _seen) = (_seen, _known);
		return _records.AsSpan(0, count);
	}

	/// <summary>The program name of a record of the latest <see cref="Take"/>, such as "code".</summary>
	public ReadOnlySpan<char> NameOf(in ProcessRecord record) => _names.AsSpan(record.NameOffset, record.NameLength);

	/// <summary>The full path of a process image, or null when the process cannot be queried.</summary>
	public static string? ImagePath(int processId)
	{
		try
		{
			return new FileInfo($"/proc/{processId}/exe").LinkTarget;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>The boot as a FILETIME (100 ns units since 1601), from the "btime" line of <c>/proc/stat</c>.</summary>
	private static long ReadBootFileTime()
	{
		try
		{
			LinuxProc.ParseCpu(File.ReadAllText("/proc/stat"), [], out var bootTime);
			return DateTimeOffset.FromUnixTimeSeconds(bootTime).UtcDateTime.ToFileTimeUtc();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
		{
			return 0;
		}
	}

	private static bool IsNumber(ReadOnlySpan<char> name) => !name.IsEmpty && name.IndexOfAnyExceptInRange('0', '9') < 0;

	private static int CountOpenFiles(string folder)
	{
		try
		{
			return Directory.EnumerateFileSystemEntries(folder + "fd").Count();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
	}

	/// <summary>
	/// The kernel keeps 15 bytes of the command name; a longer one ("chrome_crashpad") is taken from the program path of the
	/// command line when that starts the same way.
	/// </summary>
	private string NameOf(string folder, ReadOnlySpan<byte> command)
	{
		var name = Encoding.UTF8.GetString(command);
		if (command.Length < 15)
		{
			return name;
		}

		var read = Read(folder + "cmdline");
		return read > 0 && LinuxProc.ProgramName(_buffer.AsSpan(0, read)) is { } program && program.StartsWith(name, StringComparison.Ordinal)
			? program
			: name;
	}

	/// <summary>Reads a file of <c>/proc</c> into the buffer; the bytes read, or -1 when it cannot be read.</summary>
	private int Read(string path)
	{
		try
		{
			using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			return RandomAccess.Read(handle, _buffer, 0);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return -1;
		}
	}

	private int Store(ReadOnlySpan<char> name)
	{
		if (_namesUsed + name.Length > _names.Length)
		{
			Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesUsed + name.Length));
		}

		var offset = _namesUsed;
		name.CopyTo(_names.AsSpan(offset));
		_namesUsed += name.Length;
		return offset;
	}

	private int IndexOfWatched(ReadOnlySpan<char> name)
	{
		for (var i = 0; i < WatchedNames.Length; i++)
		{
			if (WatchedNames[i].Length > 0 && name.Equals(WatchedNames[i], StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>What is kept of a process between snapshots.</summary>
	private readonly record struct Known(string Name, bool CanReadIo);
}
