using System.Buffers.Binary;
using System.Net;

using Pulse.Core.Interop;

namespace Pulse.Core.Tests.Interop;

/// <summary>The listening sockets: the Windows tables on rows built here, and the real list on this machine.</summary>
public sealed class TcpListenersTests
{
	[Fact]
	public void ParseTable_Ipv4Rows_ReadPortProcessAndLoopback()
	{
		// Arrange
		var table = new byte[4 + (2 * TcpListeners.RowSize)];
		BinaryPrimitives.WriteUInt32LittleEndian(table, 2);
		Row(table.AsSpan(4), [127, 0, 0, 1], 5000, 4242);
		Row(table.AsSpan(4 + TcpListeners.RowSize), [0, 0, 0, 0], 8080, 77);
		var listeners = new List<TcpListener>();

		// Act
		TcpListeners.ParseTable(table, ipv6: false, listeners);

		// Assert
		Assert.Equal([new TcpListener(5000, 4242, LocalOnly: true), new TcpListener(8080, 77, LocalOnly: false)], listeners);

		static void Row(Span<byte> row, byte[] address, int port, int processId)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(row, 2); // LISTEN
			address.CopyTo(row[4..]);
			row[8] = (byte) (port >> 8);
			row[9] = (byte) port;
			BinaryPrimitives.WriteInt32LittleEndian(row[20..], processId);
		}
	}

	[Fact]
	public void ParseTable_Ipv6Row_ReadsPortAfterTheAddressAndScope()
	{
		// Arrange
		var table = new byte[4 + TcpListeners.Row6Size];
		BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
		var row = table.AsSpan(4);
		row[15] = 1; // ::1
		row[20] = 3000 >> 8;
		row[21] = 3000 & 0xFF;
		BinaryPrimitives.WriteInt32LittleEndian(row[52..], 9001);
		var listeners = new List<TcpListener>();

		// Act
		TcpListeners.ParseTable(table, ipv6: true, listeners);

		// Assert
		Assert.Equal([new TcpListener(3000, 9001, LocalOnly: true)], listeners);
	}

	[Fact]
	public void ParseTable_CountPastTheEnd_StopsAtTheLastWholeRow()
	{
		// Arrange
		var table = new byte[4 + TcpListeners.RowSize + 10];
		BinaryPrimitives.WriteUInt32LittleEndian(table, 5);
		var listeners = new List<TcpListener>();

		// Act
		TcpListeners.ParseTable(table, ipv6: false, listeners);

		// Assert
		Assert.Single(listeners);
	}

	[Theory]
	[InlineData("::1", true)]
	[InlineData("::ffff:127.0.0.1", true)]
	[InlineData("::", false)]
	[InlineData("2001:db8::1", false)]
	public void IsLoopback6_OnlyLoopbackAddresses(string address, bool expected)
		=> Assert.Equal(expected, TcpListeners.IsLoopback6(IPAddress.Parse(address).GetAddressBytes()));

	[Fact]
	public void Read_ListenerOfThisProcess_IsFoundWithItsPortAndProcess()
	{
		// Arrange
		var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			var port = ((IPEndPoint) listener.LocalEndpoint).Port;

			// Act
			var found = new TcpListeners().Read();

			// Assert
			Assert.Contains(found, entry => entry.Port == port && entry.ProcessId == Environment.ProcessId && entry.LocalOnly);
		}
		finally
		{
			listener.Stop();
		}
	}
}
