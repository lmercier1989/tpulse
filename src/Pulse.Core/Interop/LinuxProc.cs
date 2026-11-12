using System.Globalization;
using System.Text;

namespace Pulse.Core.Interop;

/// <summary>The fields of <c>/proc/[pid]/stat</c> that Pulse uses; times are in clock ticks.</summary>
internal readonly record struct ProcStat(int ParentId, long UserTicks, long SystemTicks, int Threads, long StartTicks);

/// <summary>The cumulative counters of one CPU line of <c>/proc/stat</c>, in clock ticks.</summary>
internal readonly record struct CpuTimes(ulong Busy, ulong Total);

/// <summary>One line of <c>/proc/diskstats</c>: sectors of 512 bytes, and the milliseconds the device was busy.</summary>
internal readonly record struct DiskCounters(string Name, ulong SectorsRead, ulong SectorsWritten, ulong BusyMilliseconds);

/// <summary>
/// Parsers for the text files of the Linux kernel Pulse reads its metrics from (<c>/proc</c> and <c>/sys</c>). They take
/// the text and do no I/O, so they are tested on every platform.
/// </summary>
internal static class LinuxProc
{
	/// <summary>
	/// <c>/proc/[pid]/stat</c>: "1234 (name) S 1 ...". The name may contain spaces and parentheses, so the fields are
	/// counted from the last ')'. <paramref name="name"/> is the kernel's command name, cut to 15 bytes.
	/// </summary>
	public static bool TryParseStat(ReadOnlySpan<byte> text, out ProcStat stat, out ReadOnlySpan<byte> name)
	{
		stat = default;
		name = default;
		var open = text.IndexOf((byte) '(');
		var close = text.LastIndexOf((byte) ')');
		if (open < 0 || close < open || close + 2 >= text.Length)
		{
			return false;
		}

		name = text[(open + 1)..close];

		// Fields after the name, from 0: state, ppid, pgrp, session, tty, tpgid, flags, minflt, cminflt, majflt, cmajflt,
		// utime (11), stime (12), cutime, cstime, priority, nice, num_threads (17), itrealvalue, starttime (19).
		var rest = text[(close + 2)..];
		long parent = 0, user = 0, system = 0, threads = 0, start = 0;
		for (var field = 0; field <= 19; field++)
		{
			var space = rest.IndexOf((byte) ' ');
			var token = space < 0 ? rest : rest[..space];
			switch (field)
			{
				case 1: parent = Number(token); break;
				case 11: user = Number(token); break;
				case 12: system = Number(token); break;
				case 17: threads = Number(token); break;
				case 19: start = Number(token); break;
			}

			if (space < 0)
			{
				if (field < 19)
				{
					return false;
				}

				break;
			}

			rest = rest[(space + 1)..];
		}

		stat = new ProcStat((int) parent, user, system, (int) threads, start);
		return true;
	}

	/// <summary><c>/proc/[pid]/statm</c>: the resident pages minus the shared ones, the anonymous memory of the process.</summary>
	public static long PrivatePages(ReadOnlySpan<byte> text)
	{
		// size resident shared text lib data dt
		var first = text.IndexOf((byte) ' ');
		if (first < 0)
		{
			return 0;
		}

		var rest = text[(first + 1)..];
		var second = rest.IndexOf((byte) ' ');
		if (second < 0)
		{
			return 0;
		}

		var resident = Number(rest[..second]);
		rest = rest[(second + 1)..];
		var third = rest.IndexOf((byte) ' ');
		var shared = Number(third < 0 ? rest : rest[..third]);
		return Math.Max(0, resident - shared);
	}

	/// <summary><c>/proc/[pid]/io</c>: the bytes read and written through system calls, files, pipes and sockets alike.</summary>
	public static long IoBytes(ReadOnlySpan<byte> text)
		=> Field(text, "rchar:"u8) + Field(text, "wchar:"u8);

	/// <summary>
	/// The program name from <c>/proc/[pid]/cmdline</c> (arguments separated by NUL): the file name of the first
	/// argument, or null when there is none (kernel threads).
	/// </summary>
	public static string? ProgramName(ReadOnlySpan<byte> commandLine)
	{
		var end = commandLine.IndexOf((byte) 0);
		var first = end < 0 ? commandLine : commandLine[..end];
		// Some programs rewrite their arguments into one line ("postgres: writer"); the name is the first word then.
		var space = first.IndexOf((byte) ' ');
		if (space > 0 && end < 0)
		{
			first = first[..space];
		}

		var slash = first.LastIndexOf((byte) '/');
		var name = slash >= 0 ? first[(slash + 1)..] : first;
		return name.IsEmpty ? null : Encoding.UTF8.GetString(name);
	}

	/// <summary>
	/// The aggregate line ("cpu") and the per-core lines ("cpu0", ...) of <c>/proc/stat</c>, cores in the order listed;
	/// <paramref name="bootTime"/> is the "btime" line, the boot in seconds since 1970.
	/// </summary>
	public static CpuTimes ParseCpu(string text, List<CpuTimes> cores, out long bootTime)
	{
		cores.Clear();
		bootTime = 0;
		CpuTimes total = default;
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			if (line.StartsWith("cpu"))
			{
				var space = line.IndexOf(' ');
				if (space < 0)
				{
					continue;
				}

				var times = Times(line[space..]);
				if (space == 3)
				{
					total = times;
				}
				else
				{
					cores.Add(times);
				}
			}
			else if (line.StartsWith("btime "))
			{
				bootTime = (long) ParseULong(line[6..].Trim());
			}
		}

		return total;

		// user nice system idle iowait irq softirq steal guest guest_nice; guest time is already in user and nice.
		static CpuTimes Times(ReadOnlySpan<char> fields)
		{
			Span<ulong> values = stackalloc ulong[8];
			var count = 0;
			foreach (var range in fields.Split(' '))
			{
				var token = fields[range];
				if (token.IsEmpty)
				{
					continue;
				}

				values[count++] = ParseULong(token);
				if (count == values.Length)
				{
					break;
				}
			}

			ulong total = 0;
			for (var i = 0; i < count; i++)
			{
				total += values[i];
			}

			var idle = values[3] + values[4];
			return new CpuTimes(total - Math.Min(total, idle), total);
		}
	}

	/// <summary>A value of <c>/proc/meminfo</c> ("MemTotal:  32768 kB") in bytes; NaN when it is missing.</summary>
	public static double MemInfo(string text, string key)
	{
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			if (line.StartsWith(key) && line.Length > key.Length && line[key.Length] == ':')
			{
				var value = line[(key.Length + 1)..].Trim();
				var unit = value.IndexOf(' ');
				var number = ParseULong(unit < 0 ? value : value[..unit]);
				return unit >= 0 && value[(unit + 1)..].Trim().Equals("kB", StringComparison.Ordinal) ? number * 1024.0 : number;
			}
		}

		return double.NaN;
	}

	/// <summary>The lines of <c>/proc/diskstats</c> for the devices <paramref name="include"/> accepts.</summary>
	public static List<DiskCounters> ParseDiskStats(string text, Func<string, bool> include)
	{
		var disks = new List<DiskCounters>();
		Span<Range> fields = stackalloc Range[14];
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			// major minor name reads merged sectors ms writes merged sectors ms in-flight io-ticks ...
			var trimmed = line.Trim();
			var count = SplitWords(trimmed, fields);
			if (count < 13)
			{
				continue;
			}

			var name = trimmed[fields[2]].ToString();
			if (include(name))
			{
				disks.Add(new DiskCounters(name, ParseULong(trimmed[fields[5]]), ParseULong(trimmed[fields[9]]), ParseULong(trimmed[fields[12]])));
			}
		}

		return disks;
	}

	/// <summary>
	/// The received and sent bytes of every interface in <c>/proc/net/dev</c>: "  eth0: rx-bytes packets ... tx-bytes ...".
	/// </summary>
	public static List<(string Name, ulong Received, ulong Sent)> ParseNetDev(string text)
	{
		var interfaces = new List<(string, ulong, ulong)>();
		Span<Range> fields = stackalloc Range[10];
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			var colon = line.IndexOf(':');
			if (colon < 0)
			{
				continue;
			}

			var values = line[(colon + 1)..].Trim();
			if (SplitWords(values, fields) < 9)
			{
				continue;
			}

			interfaces.Add((line[..colon].Trim().ToString(), ParseULong(values[fields[0]]), ParseULong(values[fields[8]])));
		}

		return interfaces;
	}

	/// <summary>
	/// The listening sockets of <c>/proc/net/tcp</c> (or <c>tcp6</c>): state 0A, with their port, whether they are bound to
	/// a loopback address, and the inode that ties them to a process ("socket:[inode]" in <c>/proc/[pid]/fd</c>). An
	/// address is written as 32-bit words in the byte order of the machine (little endian on every Linux .NET runs on), so
	/// 127.0.0.1 reads "0100007F", ::1 "00000000000000000000000001000000".
	/// </summary>
	public static List<(int Port, bool LocalOnly, long Inode)> ParseTcpListeners(string text, bool ipv6)
	{
		const string Listen = "0A";
		var listeners = new List<(int, bool, long)>();
		Span<Range> fields = stackalloc Range[10];
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			if (SplitWords(line, fields) < 10 || !line[fields[3]].Equals(Listen, StringComparison.Ordinal))
			{
				continue;
			}

			var local = line[fields[1]];
			var colon = local.IndexOf(':');
			if (colon < 0
				|| !int.TryParse(local[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)
				|| !long.TryParse(line[fields[9]], NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
			{
				continue;
			}

			var address = local[..colon];
			var loopback = ipv6
				? address.Equals("00000000000000000000000001000000", StringComparison.Ordinal)
				  || (address.Length == 32 && address[..16].IndexOfAnyExcept('0') < 0 && address[16..24].Equals("FFFF0000", StringComparison.OrdinalIgnoreCase) && address[30..].Equals("7F", StringComparison.OrdinalIgnoreCase))
				: address.Length == 8 && address[6..].Equals("7F", StringComparison.OrdinalIgnoreCase);
			listeners.Add((port, loopback, inode));
		}

		return listeners;
	}

	/// <summary>"CachyOS" or "Ubuntu 24.04.1 LTS": PRETTY_NAME of <c>/etc/os-release</c>, else NAME and VERSION.</summary>
	public static string? OsReleaseName(string text)
	{
		string? pretty = null, name = null, version = null;
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			var equals = line.IndexOf('=');
			if (equals <= 0)
			{
				continue;
			}

			var value = line[(equals + 1)..].Trim().Trim('"').Trim('\'').ToString();
			switch (line[..equals].Trim())
			{
				case "PRETTY_NAME": pretty = value; break;
				case "NAME": name = value; break;
				case "VERSION": version = value; break;
			}
		}

		return !string.IsNullOrWhiteSpace(pretty) ? pretty
			: string.IsNullOrWhiteSpace(name) ? null
			: string.IsNullOrWhiteSpace(version) ? name
			: $"{name} {version}";
	}

	/// <summary>The processor name of <c>/proc/cpuinfo</c>: "model name" on x86, "Model" or "Hardware" on ARM boards.</summary>
	public static string CpuName(string text)
	{
		string? model = null, board = null, hardware = null;
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			var colon = line.IndexOf(':');
			if (colon <= 0)
			{
				continue;
			}

			var key = line[..colon].Trim();
			var value = line[(colon + 1)..].Trim().ToString();
			if (model is null && key.Equals("model name", StringComparison.OrdinalIgnoreCase))
			{
				model = value;
			}
			else if (board is null && key.Equals("Model", StringComparison.Ordinal))
			{
				board = value;
			}
			else if (hardware is null && key.Equals("Hardware", StringComparison.Ordinal))
			{
				hardware = value;
			}
		}

		return model ?? board ?? hardware ?? string.Empty;
	}

	/// <summary>
	/// The mounted file systems of <c>/proc/mounts</c> that hold files a person keeps: a block device, not a loop device
	/// (snaps, images), and not a read-only image file system. Mount points are unescaped ("\040" is a space).
	/// </summary>
	public static List<(string Device, string MountPoint, string Type)> ParseMounts(string text)
	{
		var mounts = new List<(string, string, string)>();
		Span<Range> fields = stackalloc Range[4];
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			if (SplitWords(line, fields) < 3)
			{
				continue;
			}

			var device = line[fields[0]];
			var type = line[fields[2]];
			if (!device.StartsWith("/dev/")
				|| device.StartsWith("/dev/loop")
				|| type is "squashfs" or "iso9660" or "udf" or "erofs" or "overlay" or "tmpfs")
			{
				continue;
			}

			mounts.Add((device.ToString(), Unescape(line[fields[1]]), type.ToString()));
		}

		return mounts;

		static string Unescape(ReadOnlySpan<char> path)
		{
			if (!path.Contains('\\'))
			{
				return path.ToString();
			}

			var builder = new StringBuilder(path.Length);
			for (var i = 0; i < path.Length; i++)
			{
				if (path[i] == '\\' && i + 3 < path.Length && path.Slice(i + 1, 3).IndexOfAnyExceptInRange('0', '7') < 0)
				{
					builder.Append((char) (((path[i + 1] - '0') * 64) + ((path[i + 2] - '0') * 8) + (path[i + 3] - '0')));
					i += 3;
				}
				else
				{
					builder.Append(path[i]);
				}
			}

			return builder.ToString();
		}
	}

	/// <summary>
	/// The name of a PCI device from a <c>pci.ids</c> database: the subsystem's own name when it is listed, else the
	/// device's; a marketing name in brackets ("Navi 44 [Radeon RX 9060 XT]") is preferred. Null when it is not listed.
	/// </summary>
	public static string? PciDeviceName(IEnumerable<string> lines, uint vendor, uint device, uint subVendor, uint subDevice)
	{
		var vendorKey = vendor.ToString("x4", CultureInfo.InvariantCulture);
		var deviceKey = device.ToString("x4", CultureInfo.InvariantCulture);
		var subsystemKey = $"{subVendor:x4} {subDevice:x4}";
		var inVendor = false;
		string? deviceName = null;
		foreach (var line in lines)
		{
			if (line.Length == 0 || line[0] == '#')
			{
				continue;
			}

			if (line[0] != '\t')
			{
				if (inVendor)
				{
					break;
				}

				inVendor = line.StartsWith(vendorKey, StringComparison.OrdinalIgnoreCase) && line.Length > 4 && line[4] == ' ';
				continue;
			}

			if (!inVendor)
			{
				continue;
			}

			if (line.Length > 1 && line[1] != '\t')
			{
				if (deviceName is not null)
				{
					break; // The next device: the subsystems of ours are behind us.
				}

				if (line.AsSpan(1).StartsWith(deviceKey, StringComparison.OrdinalIgnoreCase))
				{
					deviceName = line[(1 + 4)..].Trim();
				}
			}
			else if (deviceName is not null && line.AsSpan(2).StartsWith(subsystemKey, StringComparison.OrdinalIgnoreCase))
			{
				return Marketing(line[(2 + 9)..].Trim());
			}
		}

		return deviceName is null ? null : Marketing(deviceName);

		static string Marketing(string name)
		{
			var open = name.LastIndexOf('[');
			var close = name.LastIndexOf(']');
			return open >= 0 && close > open + 1 ? name[(open + 1)..close] : name;
		}
	}

	/// <summary>A hexadecimal id of <c>/sys</c> ("0x1002"); 0 when it cannot be read.</summary>
	public static uint HexId(string? text)
	{
		var value = (text ?? string.Empty).Trim();
		if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			value = value[2..];
		}

		return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : 0;
	}

	internal static ulong ParseULong(ReadOnlySpan<char> text)
		=> ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

	private static long Number(ReadOnlySpan<byte> text)
	{
		long value = 0;
		var negative = !text.IsEmpty && text[0] == '-';
		foreach (var b in negative ? text[1..] : text)
		{
			if (b is < (byte) '0' or > (byte) '9')
			{
				break;
			}

			value = (value * 10) + (b - '0');
		}

		return negative ? -value : value;
	}

	/// <summary>The number after "<paramref name="key"/>" at the start of a line; 0 when there is none.</summary>
	private static long Field(ReadOnlySpan<byte> text, ReadOnlySpan<byte> key)
	{
		var at = text.IndexOf(key);
		while (at > 0 && text[at - 1] != '\n')
		{
			var next = text[(at + 1)..].IndexOf(key);
			at = next < 0 ? -1 : at + 1 + next;
		}

		if (at < 0)
		{
			return 0;
		}

		var value = text[(at + key.Length)..].TrimStart((byte) ' ');
		return Number(value);
	}

	/// <summary>Splits on runs of spaces and tabs; returns how many words were found (at most the length of the ranges).</summary>
	private static int SplitWords(ReadOnlySpan<char> line, Span<Range> words)
	{
		var count = 0;
		var i = 0;
		while (i < line.Length && count < words.Length)
		{
			while (i < line.Length && line[i] is ' ' or '\t')
			{
				i++;
			}

			if (i == line.Length)
			{
				break;
			}

			var start = i;
			while (i < line.Length && line[i] is not (' ' or '\t'))
			{
				i++;
			}

			words[count++] = start..i;
		}

		return count;
	}
}
