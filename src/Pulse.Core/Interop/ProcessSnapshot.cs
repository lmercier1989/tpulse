using System.Runtime.InteropServices;

namespace Pulse.Core.Interop;

/// <summary>
/// All processes of the machine from one <c>NtQuerySystemInformation(SystemProcessInformation)</c> call: id, parent,
/// creation time, CPU time, private memory, threads and handles, without opening a handle to any process. The buffers
/// are reused, so taking a snapshot every second does not allocate once warmed up. 64-bit processes only (the offsets
/// below are those of the 64-bit SYSTEM_PROCESS_INFORMATION; a unit test checks them against the running system).
/// </summary>
internal sealed unsafe class ProcessSnapshot
{
	internal const int NextEntryOffset = 0;
	internal const int ThreadCountOffset = 4;
	internal const int PrivateWorkingSetOffset = 8;
	internal const int CreateTimeOffset = 32;
	internal const int UserTimeOffset = 40;
	internal const int KernelTimeOffset = 48;
	internal const int ImageNameLengthOffset = 56;
	internal const int ImageNameBufferOffset = 64;
	internal const int ProcessIdOffset = 80;
	internal const int ParentIdOffset = 88;
	internal const int HandleCountOffset = 96;
	internal const int ReadTransferOffset = 232;
	internal const int WriteTransferOffset = 240;

	private const int SystemProcessInformation = 5;
	private const int StatusInfoLengthMismatch = unchecked((int) 0xC0000004);

	private byte[] _buffer = new byte[1024 * 1024];
	private ProcessRecord[] _records = new ProcessRecord[1024];

	public static bool IsSupported => IntPtr.Size == 8;

	/// <summary>Image names to mark in <see cref="ProcessRecord.Watch"/>, such as "devenv.exe"; compared ignoring case.</summary>
	public string[] WatchedNames { get; set; } = [];

	/// <summary>Takes a snapshot; empty when the system call fails. Valid until the next call.</summary>
	public ReadOnlySpan<ProcessRecord> Take()
	{
		for (var attempt = 0; attempt < 4; attempt++)
		{
			int status;
			uint needed;
			fixed (byte* buffer = _buffer)
			{
				status = Native.NtQuerySystemInformation(SystemProcessInformation, buffer, (uint) _buffer.Length, out needed);
			}

			if (status == StatusInfoLengthMismatch)
			{
				_buffer = new byte[Math.Max(needed + (64 * 1024), (uint) _buffer.Length * 2)];
				continue;
			}

			return status < 0 ? default : Parse();
		}

		return default;
	}

	/// <summary>The image name of a record of the latest <see cref="Take"/>, such as "devenv.exe"; empty for the idle process.</summary>
	public ReadOnlySpan<char> NameOf(in ProcessRecord record)
		=> record.NameLength == 0 ? default : MemoryMarshal.Cast<byte, char>(_buffer.AsSpan(record.NameOffset, record.NameLength * 2));

	/// <summary>The full path of a process image, or null when the process cannot be queried.</summary>
	public static string? ImagePath(int processId)
	{
		const uint QueryLimitedInformation = 0x1000;
		var handle = Native.OpenProcess(QueryLimitedInformation, false, (uint) processId);
		if (handle == 0)
		{
			return null;
		}

		try
		{
			Span<char> path = stackalloc char[1024];
			var size = (uint) path.Length;
			fixed (char* buffer = path)
			{
				return Native.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int) size) : null;
			}
		}
		finally
		{
			_ = Native.CloseHandle(handle);
		}
	}

	private ReadOnlySpan<ProcessRecord> Parse()
	{
		var count = 0;
		fixed (byte* start = _buffer)
		{
			var offset = 0;
			while (offset + WriteTransferOffset + 8 <= _buffer.Length)
			{
				var entry = start + offset;
				if (count == _records.Length)
				{
					Array.Resize(ref _records, _records.Length * 2);
				}

				ref var record = ref _records[count++];
				record.Id = (int) *(long*) (entry + ProcessIdOffset);
				record.ParentId = (int) *(long*) (entry + ParentIdOffset);
				record.CreateTime = *(long*) (entry + CreateTimeOffset);
				record.CpuTime = *(long*) (entry + UserTimeOffset) + *(long*) (entry + KernelTimeOffset);
				record.PrivateBytes = *(long*) (entry + PrivateWorkingSetOffset);
				record.Threads = (int) *(uint*) (entry + ThreadCountOffset);
				record.Handles = (int) *(uint*) (entry + HandleCountOffset);
				record.IoBytes = *(long*) (entry + ReadTransferOffset) + *(long*) (entry + WriteTransferOffset);
				var nameLength = *(ushort*) (entry + ImageNameLengthOffset) / 2;
				var buffer = *(char**) (entry + ImageNameBufferOffset);
				var name = buffer == null ? default : new ReadOnlySpan<char>(buffer, nameLength);

				// The names lie inside the same buffer; keep their place, not the pointer, because the buffer moves once unpinned.
				var inside = buffer != null && (byte*) buffer >= start && (byte*) buffer + (nameLength * 2) <= start + _buffer.Length;
				record.NameOffset = inside ? (int) ((byte*) buffer - start) : 0;
				record.NameLength = inside ? nameLength : 0;
				record.IsClaude = name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase);
				record.Watch = IndexOfWatched(name);

				var next = *(uint*) (entry + NextEntryOffset);
				if (next == 0)
				{
					break;
				}

				offset += (int) next;
			}
		}

		return _records.AsSpan(0, count);
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
}
