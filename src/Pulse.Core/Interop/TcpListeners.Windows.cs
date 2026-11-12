namespace Pulse.Core.Interop;

/// <summary>The Windows part of <see cref="TcpListeners"/>: <c>GetExtendedTcpTable</c> for IPv4 and IPv6, with the owning process.</summary>
internal sealed partial class TcpListeners
{
	private const uint InterNetwork = 2;
	private const uint InterNetworkV6 = 23;

	/// <summary>TCP_TABLE_OWNER_PID_LISTENER: the listening sockets only, with the process that owns each.</summary>
	private const int OwnerPidListener = 3;

	private const uint ErrorInsufficientBuffer = 122;

	private byte[] _buffer = new byte[16 * 1024];

	/// <summary>Every listening TCP socket of this machine; empty when the table cannot be read.</summary>
	public List<TcpListener> Read()
	{
		var listeners = new List<TcpListener>();
		Read(InterNetwork, listeners);
		Read(InterNetworkV6, listeners);
		return listeners;
	}

	private unsafe void Read(uint family, List<TcpListener> listeners)
	{
		for (var attempt = 0; attempt < 4; attempt++)
		{
			var size = (uint) _buffer.Length;
			uint result;
			fixed (byte* table = _buffer)
			{
				result = Native.GetExtendedTcpTable(table, ref size, false, family, OwnerPidListener, 0);
			}

			if (result == ErrorInsufficientBuffer)
			{
				_buffer = new byte[Math.Max(size + 4096, (uint) _buffer.Length * 2)];
				continue;
			}

			if (result == Native.ErrorSuccess)
			{
				ParseTable(_buffer.AsSpan(0, (int) Math.Min(size, (uint) _buffer.Length)), family == InterNetworkV6, listeners);
			}

			return;
		}
	}
}
