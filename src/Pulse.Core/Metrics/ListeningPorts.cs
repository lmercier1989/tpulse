using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>A TCP port a program listens on: a dev server, a database, a debugger.</summary>
/// <param name="Port">The port number.</param>
/// <param name="ProcessId">The process that listens.</param>
/// <param name="Process">Its name, without ".exe".</param>
/// <param name="LocalOnly">Bound to the loopback address only, so only this machine can connect.</param>
public sealed record ListeningPort(int Port, int ProcessId, string Process, bool LocalOnly)
{
	/// <summary>"http://localhost:5000": where a browser finds it, if it speaks HTTP.</summary>
	public string Url => $"http://localhost:{Port}";
}

/// <summary>The ports listened on at one moment.</summary>
/// <param name="Ports">The ports of programs, the usual ones first (3000, 5000, 8080), then the ones the system hands out.</param>
/// <param name="SystemPorts">How many ports the operating system itself listens on (RPC, file sharing); only counted.</param>
public sealed record PortActivity(IReadOnlyList<ListeningPort> Ports, int SystemPorts);

/// <summary>
/// The TCP ports listened on, by program, from <see cref="TcpListeners"/> and a process snapshot for the names. The
/// operating system's own listeners (System, svchost, lsass and other programs from the Windows folder; on Linux the
/// sockets of other users' processes, such as sshd or cupsd) are counted, not listed, so the list shows what you started.
/// </summary>
internal sealed class ListeningPorts
{
	/// <summary>
	/// Processes of Windows that listen: RPC, file sharing, the service host, Hyper-V, the Intel driver service. They run as
	/// services an account without administrator rights cannot look into, so their folder cannot tell.
	/// </summary>
	private static readonly HashSet<string> WindowsOwn = new(StringComparer.OrdinalIgnoreCase)
	{
		"System", "System Idle", "svchost", "lsass", "wininit", "services", "spoolsv", "smss", "csrss", "vmms", "vmcompute", "jhi_service",
	};

	private static readonly string WindowsFolder = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : string.Empty;

	private readonly TcpListeners _listeners = new();
	private readonly Dictionary<(int Id, long Created), bool> _fromWindows = [];
	private readonly Dictionary<int, int> _indexById = [];

	/// <summary>
	/// The first port of the range the system hands out for a moment (49152 on Windows, 32768 on Linux): ports from there
	/// are listed after the ones a program asked for.
	/// </summary>
	internal static int DynamicStart => OperatingSystem.IsWindows() ? 49152 : 32768;

	/// <summary>The ports listened on now, named from <paramref name="records"/>, a snapshot of every process taken by <paramref name="snapshot"/>.</summary>
	public PortActivity Sample(ReadOnlySpan<ProcessRecord> records, ProcessSnapshot snapshot)
	{
		var listeners = _listeners.Read();
		_indexById.Clear();
		for (var i = 0; i < records.Length; i++)
		{
			_indexById[records[i].Id] = i;
		}

		var owners = new Dictionary<int, (string Name, bool System)>();
		foreach (var listener in listeners)
		{
			if (owners.ContainsKey(listener.ProcessId) || !_indexById.TryGetValue(listener.ProcessId, out var index))
			{
				continue;
			}

			ref readonly var record = ref records[index];
			var name = TopProcesses.Clean(snapshot.NameOf(record));
			owners[listener.ProcessId] = (name, IsSystem(record, name));
		}

		if (_fromWindows.Count > 512)
		{
			_fromWindows.Clear(); // Forget processes that ended; live ones are looked up again.
		}

		return Group(listeners, id => owners.TryGetValue(id, out var owner) ? owner : null);
	}

	/// <summary>
	/// One row per port and process: a port listened on over IPv4 and IPv6 counts once, local only when every one of its
	/// sockets is. A socket whose process is not known, or belongs to the system, is only counted.
	/// </summary>
	internal static PortActivity Group(IEnumerable<TcpListener> listeners, Func<int, (string Name, bool System)?> owner)
	{
		var ports = new Dictionary<(int Port, int Id), ListeningPort>();
		var system = new HashSet<int>();
		foreach (var listener in listeners)
		{
			if (listener.ProcessId <= 0 || owner(listener.ProcessId) is not { System: false } known)
			{
				system.Add(listener.Port);
				continue;
			}

			var key = (listener.Port, listener.ProcessId);
			ports[key] = ports.TryGetValue(key, out var seen)
				? seen with { LocalOnly = seen.LocalOnly && listener.LocalOnly }
				: new ListeningPort(listener.Port, listener.ProcessId, known.Name, listener.LocalOnly);
		}

		system.ExceptWith(ports.Keys.Select(key => key.Port));
		var ordered = ports.Values.OrderBy(port => port.Port >= DynamicStart).ThenBy(port => port.Port).ThenBy(port => port.Process, StringComparer.OrdinalIgnoreCase).ToList();
		return new PortActivity(ordered, system.Count);
	}

	/// <summary>
	/// Whether a process is part of the operating system: on Windows one of its known listeners or a program in the Windows
	/// folder (looked up once per process); on Linux every process Pulse can name is the user's own.
	/// </summary>
	private bool IsSystem(in ProcessRecord record, string name)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		if (record.Id <= 4 || WindowsOwn.Contains(name))
		{
			return true;
		}

		var key = (record.Id, record.CreateTime);
		if (!_fromWindows.TryGetValue(key, out var fromWindows))
		{
			// A process that cannot be looked into (a service of another account) is not assumed to be Windows: SQL Server
			// and PostgreSQL run that way, and those are the ports a developer looks for.
			fromWindows = ProcessSnapshot.ImagePath(record.Id) is { } path && WindowsFolder.Length > 0
				&& path.StartsWith(WindowsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
			_fromWindows[key] = fromWindows;
		}

		return fromWindows;
	}
}
