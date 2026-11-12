using System.Runtime.InteropServices;

namespace Pulse.Core.Interop;

/// <summary>
/// The current folder of another process, read from its process parameters (the same place Process Explorer shows it):
/// a shell, an editor, a build or an agent working in a repository tells Pulse where that repository is. Works for the
/// processes of the same user without administrator rights; 64-bit processes only (the offsets are those of the
/// 64-bit PEB and RTL_USER_PROCESS_PARAMETERS, unchanged since Windows Vista).
/// </summary>
internal static unsafe class ProcessDirectory
{
	internal const int ProcessParametersOffset = 0x20;
	internal const int CurrentDirectoryLengthOffset = 0x38;
	internal const int CurrentDirectoryBufferOffset = 0x40;

	private const uint QueryLimitedInformation = 0x1000;
	private const uint VirtualMemoryRead = 0x0010;
	private const int ProcessBasicInformationClass = 0;
	private const int MaximumBytes = 2 * 1024;

	/// <summary>The current folder of the process, or null when it cannot be read (another user, elevated, gone).</summary>
	public static string? TryRead(int processId)
	{
		var handle = Native.OpenProcess(QueryLimitedInformation | VirtualMemoryRead, false, (uint) processId);
		if (handle == 0)
		{
			return null;
		}

		try
		{
			ProcessBasicInformation basic;
			if (Native.NtQueryInformationProcess(handle, ProcessBasicInformationClass, &basic, (uint) sizeof(ProcessBasicInformation), out _) != 0
				|| basic.PebBaseAddress == 0)
			{
				return null;
			}

			nint parameters;
			ushort length;
			nint buffer;
			if (!Read(handle, basic.PebBaseAddress + ProcessParametersOffset, &parameters, (nuint) sizeof(nint))
				|| parameters == 0
				|| !Read(handle, parameters + CurrentDirectoryLengthOffset, &length, sizeof(ushort))
				|| !Read(handle, parameters + CurrentDirectoryBufferOffset, &buffer, (nuint) sizeof(nint))
				|| length == 0
				|| length > MaximumBytes
				|| buffer == 0)
			{
				return null;
			}

			var text = stackalloc char[length / 2];
			return Read(handle, buffer, text, length) ? new string(text, 0, length / 2) : null;
		}
		finally
		{
			_ = Native.CloseHandle(handle);
		}
	}

	private static bool Read(nint process, nint address, void* target, nuint size)
		=> Native.ReadProcessMemory(process, address, target, size, out var read) && read == size;

	/// <summary>PROCESS_BASIC_INFORMATION of a 64-bit process.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessBasicInformation
	{
		public nint ExitStatus;
		public nint PebBaseAddress;
		public nint AffinityMask;
		public nint BasePriority;
		public nint UniqueProcessId;
		public nint InheritedFromUniqueProcessId;
	}
}
