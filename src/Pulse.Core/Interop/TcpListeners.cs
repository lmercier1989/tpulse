using System.Buffers.Binary;

namespace Pulse.Core.Interop;

/// <summary>One TCP socket in the listening state: its port, the process that owns it, and whether it takes connections from this machine only.</summary>
/// <param name="Port">The local port.</param>
/// <param name="ProcessId">The owning process; 0 when it is not known (on Linux, a socket of another user's process).</param>
/// <param name="LocalOnly">Bound to a loopback address (127.0.0.1, ::1), so only this machine can connect.</param>
internal readonly record struct TcpListener(int Port, int ProcessId, bool LocalOnly);

/// <summary>
/// The TCP ports processes listen on, what <c>netstat -ano</c> lists as LISTENING, read without administrator rights. The
/// reading itself is per platform (TcpListeners.Windows.cs, TcpListeners.Linux.cs); the parsing of the Windows table is
/// here, so it is tested on every platform.
/// </summary>
internal sealed partial class TcpListeners
{
	/// <summary>MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, owning process.</summary>
	internal const int RowSize = 24;

	/// <summary>
	/// MIB_TCP6ROW_OWNER_PID: local address (16), scope, local port, remote address (16), scope, remote port, state, owning
	/// process.
	/// </summary>
	internal const int Row6Size = 56;

	/// <summary>
	/// The rows of a MIB_TCPTABLE_OWNER_PID (or, with <paramref name="ipv6"/>, a MIB_TCP6TABLE_OWNER_PID): a count, then
	/// the rows. A port is kept in network byte order in the low two bytes of its field.
	/// </summary>
	internal static void ParseTable(ReadOnlySpan<byte> table, bool ipv6, List<TcpListener> listeners)
	{
		if (table.Length < 4)
		{
			return;
		}

		var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
		var size = ipv6 ? Row6Size : RowSize;
		for (var i = 0L; i < count; i++)
		{
			var offset = 4 + (i * size);
			if (offset + size > table.Length)
			{
				break;
			}

			var row = table.Slice((int) offset, size);
			listeners.Add(ipv6
				? new TcpListener(Port(row[20..]), BinaryPrimitives.ReadInt32LittleEndian(row[52..]), IsLoopback6(row[..16]))
				: new TcpListener(Port(row[8..]), BinaryPrimitives.ReadInt32LittleEndian(row[20..]), row[4] == 127));
		}
	}

	/// <summary>::1, or 127.x.x.x mapped into IPv6 (::ffff:127.0.0.1).</summary>
	internal static bool IsLoopback6(ReadOnlySpan<byte> address)
		=> address.Length == 16
		   && ((address[..15].IndexOfAnyExcept((byte) 0) < 0 && address[15] == 1)
			   || (address[..10].IndexOfAnyExcept((byte) 0) < 0 && address[10] == 0xFF && address[11] == 0xFF && address[12] == 127));

	private static int Port(ReadOnlySpan<byte> field) => (field[0] << 8) | field[1];
}
