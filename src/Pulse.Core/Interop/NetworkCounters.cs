using System.Diagnostics;

using Pulse.Core.Processing;

namespace Pulse.Core.Interop;

/// <summary>
/// Network throughput from the IP Helper interface table (<c>GetIfTable2</c>). Only physical interfaces
/// that are up count: virtual switches (Hyper-V, WSL, Docker), filter drivers, tunnels and loopback carry
/// traffic that also passes a physical adapter, and summing them would count it twice.
/// </summary>
internal sealed unsafe class NetworkCounters
{
	private readonly CounterRate _rate = new();
	private long _lastTimestamp;

	/// <summary>Download and upload in bits per second; NaN on the first call.</summary>
	public (double DownBitsPerSecond, double UpBitsPerSecond) Sample()
	{
		if (Native.GetIfTable2(out var table) != Native.ErrorSuccess || table == 0)
		{
			return (double.NaN, double.NaN);
		}

		try
		{
			var timestamp = Stopwatch.GetTimestamp();
			var count = *(uint*) table;
			var rows = (MibIfRow2*) ((byte*) table + 8);

			_rate.Begin();
			for (var i = 0; i < count; i++)
			{
				if (IsPhysicalAndUp(rows[i]))
				{
					_rate.Add(rows[i].InterfaceLuid, rows[i].InOctets, rows[i].OutOctets);
				}
			}

			var elapsed = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds;
			_lastTimestamp = timestamp;
			var (inBytes, outBytes) = _rate.Complete(elapsed);
			return (inBytes * 8, outBytes * 8);
		}
		finally
		{
			Native.FreeMibTable(table);
		}
	}

	/// <summary>Descriptions of the interfaces that are counted, for display and diagnostics.</summary>
	public static List<string> DescribeCountedInterfaces()
	{
		var names = new List<string>();
		if (Native.GetIfTable2(out var table) != Native.ErrorSuccess || table == 0)
		{
			return names;
		}

		try
		{
			var count = *(uint*) table;
			var rows = (MibIfRow2*) ((byte*) table + 8);
			for (var i = 0; i < count; i++)
			{
				if (IsPhysicalAndUp(rows[i]))
				{
					names.Add(new string(rows[i].Description).Trim());
				}
			}
		}
		finally
		{
			Native.FreeMibTable(table);
		}

		return names;
	}

	private static bool IsPhysicalAndUp(in MibIfRow2 row)
		=> (row.InterfaceAndOperStatusFlags & MibIfRow2.HardwareInterfaceFlag) != 0
		   && (row.InterfaceAndOperStatusFlags & MibIfRow2.FilterInterfaceFlag) == 0
		   && row.OperStatus == MibIfRow2.OperStatusUp
		   && row.Type != MibIfRow2.SoftwareLoopbackType;
}
