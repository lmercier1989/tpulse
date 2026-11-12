using System.Runtime.InteropServices;

namespace Pulse.Core.Interop;

/// <summary>
/// P/Invoke declarations for the Windows APIs Pulse reads metrics from. All entry points are
/// source-generated (<see cref="LibraryImportAttribute"/>) so no marshalling stubs are built at run time.
/// </summary>
internal static partial class Native
{
	internal const uint ErrorSuccess = 0;

	// PDH status and format constants (pdhmsg.h / pdh.h).
	internal const uint PdhMoreData = 0x800007D2;
	internal const uint PdhFmtDouble = 0x00000200;
	internal const uint PdhFmtNoCap100 = 0x00008000;
	internal const uint PdhCStatusValidData = 0x00000000;
	internal const uint PdhCStatusNewData = 0x00000001;

	[LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
	internal static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

	[LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
	internal static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

	[LibraryImport("pdh.dll")]
	internal static partial uint PdhCollectQueryData(nint query);

	[LibraryImport("pdh.dll")]
	internal static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

	[LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
	internal static unsafe partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* itemBuffer);

	[LibraryImport("pdh.dll")]
	internal static partial uint PdhCloseQuery(nint query);

	/// <summary>Connects to the performance counters of another machine (through its Remote Registry service).</summary>
	[LibraryImport("pdh.dll", EntryPoint = "PdhConnectMachineW", StringMarshalling = StringMarshalling.Utf16)]
	internal static partial uint PdhConnectMachine(string? machineName);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool GetSystemPowerStatus(out Metrics.SystemPowerStatus status);

	/// <summary>SystemProcessInformation (5): every process with its parent, CPU times and memory, in one call.</summary>
	[LibraryImport("ntdll.dll")]
	internal static unsafe partial int NtQuerySystemInformation(int informationClass, void* buffer, uint length, out uint returnLength);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

	/// <summary>ProcessBasicInformation (0): where the process environment block of another process lies.</summary>
	[LibraryImport("ntdll.dll")]
	internal static unsafe partial int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, out uint returnLength);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static unsafe partial bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

	[LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool CloseHandle(nint handle);

	[LibraryImport("iphlpapi.dll")]
	internal static partial uint GetIfTable2(out nint table);

	[LibraryImport("iphlpapi.dll")]
	internal static partial void FreeMibTable(nint memory);

	/// <summary>The TCP sockets of this machine with the process that owns each (TCP_TABLE_OWNER_PID_*), as netstat -ano lists them.</summary>
	[LibraryImport("iphlpapi.dll")]
	internal static unsafe partial uint GetExtendedTcpTable(void* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, int tableClass, uint reserved);

	[LibraryImport("dxgi.dll")]
	internal static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

	[LibraryImport("gdi32.dll")]
	internal static partial int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid args);

	[LibraryImport("gdi32.dll")]
	internal static partial int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo args);

	[LibraryImport("gdi32.dll")]
	internal static partial int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter args);
}

/// <summary>PDH_FMT_COUNTERVALUE: a status followed by an 8-byte aligned value union.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PdhFmtCounterValue
{
	[FieldOffset(0)] public uint CStatus;
	[FieldOffset(8)] public double DoubleValue;
}

/// <summary>PDH_FMT_COUNTERVALUE_ITEM_W: instance name pointer plus its value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValueItem
{
	public nint Name;
	public PdhFmtCounterValue Value;
}

/// <summary>MEMORYSTATUSEX (64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
	public uint Length;
	public uint MemoryLoad;
	public ulong TotalPhys;
	public ulong AvailPhys;
	public ulong TotalPageFile;
	public ulong AvailPageFile;
	public ulong TotalVirtual;
	public ulong AvailVirtual;
	public ulong AvailExtendedVirtual;
}

/// <summary>MIB_IF_ROW2 from netioapi.h (1352 bytes); the layout is pinned by a unit test.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MibIfRow2
{
	public const byte HardwareInterfaceFlag = 0x01;
	public const byte FilterInterfaceFlag = 0x02;
	public const uint OperStatusUp = 1;
	public const uint SoftwareLoopbackType = 24;

	public ulong InterfaceLuid;
	public uint InterfaceIndex;
	public Guid InterfaceGuid;
	public fixed char Alias[257];
	public fixed char Description[257];
	public uint PhysicalAddressLength;
	public fixed byte PhysicalAddress[32];
	public fixed byte PermanentPhysicalAddress[32];
	public uint Mtu;
	public uint Type;
	public uint TunnelType;
	public uint MediaType;
	public uint PhysicalMediumType;
	public uint AccessType;
	public uint DirectionType;
	public byte InterfaceAndOperStatusFlags;
	public uint OperStatus;
	public uint AdminStatus;
	public uint MediaConnectState;
	public Guid NetworkGuid;
	public uint ConnectionType;
	public ulong TransmitLinkSpeed;
	public ulong ReceiveLinkSpeed;
	public ulong InOctets;
	public ulong InUcastPkts;
	public ulong InNUcastPkts;
	public ulong InDiscards;
	public ulong InErrors;
	public ulong InUnknownProtos;
	public ulong InUcastOctets;
	public ulong InMulticastOctets;
	public ulong InBroadcastOctets;
	public ulong OutOctets;
	public ulong OutUcastPkts;
	public ulong OutNUcastPkts;
	public ulong OutDiscards;
	public ulong OutErrors;
	public ulong OutUcastOctets;
	public ulong OutMulticastOctets;
	public ulong OutBroadcastOctets;
	public ulong OutQLen;
}

/// <summary>DXGI_ADAPTER_DESC1.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiAdapterDesc1
{
	public const uint SoftwareFlag = 0x2;

	public fixed char Description[128];
	public uint VendorId;
	public uint DeviceId;
	public uint SubSysId;
	public uint Revision;
	public nuint DedicatedVideoMemory;
	public nuint DedicatedSystemMemory;
	public nuint SharedSystemMemory;
	public uint LuidLow;
	public int LuidHigh;
	public uint Flags;
}

/// <summary>D3DKMT_OPENADAPTERFROMLUID.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3dkmtOpenAdapterFromLuid
{
	public uint LuidLow;
	public int LuidHigh;
	public uint Adapter;
}

/// <summary>D3DKMT_QUERYADAPTERINFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D3dkmtQueryAdapterInfo
{
	/// <summary>KMTQAITYPE_ADAPTERPERFDATA: the query Task Manager uses for GPU temperature.</summary>
	public const int AdapterPerfDataType = 62;

	public uint Adapter;
	public int Type;
	public void* PrivateDriverData;
	public uint PrivateDriverDataSize;
}

/// <summary>D3DKMT_CLOSEADAPTER.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3dkmtCloseAdapter
{
	public uint Adapter;
}

/// <summary>D3DKMT_ADAPTER_PERFDATA (64 bytes); temperature is in deci-degrees Celsius.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3dkmtAdapterPerfData
{
	public uint PhysicalAdapterIndex;
	public ulong MemoryFrequency;
	public ulong MaxMemoryFrequency;
	public ulong MaxMemoryFrequencyOverclocked;
	public ulong MemoryBandwidth;
	public ulong PcieBandwidth;
	public uint FanRpm;
	public uint Power;
	public uint Temperature;
	public byte PowerStateOverride;
}
