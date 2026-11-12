namespace Pulse.Core.Interop;

/// <summary>
/// The Linux part of <see cref="TcpListeners"/>: the listening sockets of <c>/proc/net/tcp</c> and <c>tcp6</c>, tied to their
/// process through the "socket:[inode]" links in <c>/proc/[pid]/fd</c>. Those links are looked through only when a socket
/// appears that is not known yet, and only the processes of this user can be looked into; a socket of another user's
/// process keeps process id 0.
/// </summary>
internal sealed partial class TcpListeners
{
	private const string SocketPrefix = "socket:[";

	/// <summary>The process of every listening socket seen, by inode; 0 when it could not be found.</summary>
	private readonly Dictionary<long, int> _ownerByInode = [];

	/// <summary>Every listening TCP socket of this machine; empty when <c>/proc/net</c> cannot be read.</summary>
	public List<TcpListener> Read()
	{
		var found = new List<(int Port, bool LocalOnly, long Inode)>();
		foreach (var (file, ipv6) in new[] { ("/proc/net/tcp", false), ("/proc/net/tcp6", true) })
		{
			try
			{
				found.AddRange(LinuxProc.ParseTcpListeners(File.ReadAllText(file), ipv6));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// No IPv6 on this machine, or /proc/net is not mounted: the other list still counts.
			}
		}

		if (found.Exists(entry => !_ownerByInode.ContainsKey(entry.Inode)))
		{
			FindOwners([.. found.Select(entry => entry.Inode)]);
		}

		return [.. found.Select(entry => new TcpListener(entry.Port, _ownerByInode.GetValueOrDefault(entry.Inode), entry.LocalOnly))];
	}

	/// <summary>Looks through the open files of every process this user may look into for the sockets of <paramref name="inodes"/>.</summary>
	private void FindOwners(HashSet<long> inodes)
	{
		foreach (var closed in _ownerByInode.Keys.Where(inode => !inodes.Contains(inode)).ToList())
		{
			_ownerByInode.Remove(closed);
		}

		foreach (var inode in inodes)
		{
			_ownerByInode.TryAdd(inode, 0);
		}

		foreach (var folder in Directory.EnumerateDirectories("/proc"))
		{
			if (!int.TryParse(Path.GetFileName(folder), out var id))
			{
				continue;
			}

			try
			{
				foreach (var descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(folder, "fd")))
				{
					if (new FileInfo(descriptor).LinkTarget is { } target
						&& target.StartsWith(SocketPrefix, StringComparison.Ordinal)
						&& long.TryParse(target.AsSpan(SocketPrefix.Length, target.Length - SocketPrefix.Length - 1), out var inode)
						&& inodes.Contains(inode))
					{
						_ownerByInode[inode] = id;
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Another user's process, or one that ended meanwhile.
			}
		}
	}
}
