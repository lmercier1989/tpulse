using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Pulse.Core.Diagnostics;

namespace Pulse.Terminal;

/// <summary>
/// The Windows console the monitor runs in: raw keyboard input (no echo, no line editing, Ctrl+C as a key),
/// VT output in UTF-8 on the alternate screen, terminal queries, and a high-resolution wait that also wakes
/// on a key press, so frames are paced precisely without a thread or a busy loop. Everything is restored on
/// <see cref="Exit"/>.
/// </summary>
internal sealed unsafe partial class ConsoleHost : IDisposable
{
	private const int StandardInput = -10;
	private const int StandardOutput = -11;
	private const uint EnableProcessedOutput = 0x1;
	private const uint EnableVirtualTerminalProcessing = 0x4;
	private const uint DisableNewlineAutoReturn = 0x8;
	private const uint EnableWindowInput = 0x8;
	private const uint EnableExtendedFlags = 0x80;
	private const uint EnableVirtualTerminalInput = 0x200;
	private const uint EnableMouseInput = 0x10;
	private const ushort KeyEventType = 1;
	private const ushort MouseEventType = 2;
	private const uint MouseMoved = 0x1;
	private const uint DoubleClicked = 0x2;
	private const uint MouseWheeled = 0x4;
	private const uint LeftButtonPressed = 0x1;
	private const uint RightButtonPressed = 0x2;
	private const uint ShiftPressed = 0x10;
	private const uint ControlPressed = 0x04 | 0x08;
	private const uint AltPressed = 0x01 | 0x02;
	private const uint Utf8CodePage = 65001;
	private const uint Infinite = 0xFFFFFFFF;
	private const uint HighResolutionTimer = 0x2;
	private const uint TimerAllAccess = 0x1F0003;

	private readonly nint _input;
	private readonly nint _output;
	private readonly uint _inputMode;
	private readonly uint _outputMode;
	private readonly uint _outputCodePage;
	private readonly nint _timer;
	private readonly Stream _stdout;
	private readonly InputRecord[] _records = new InputRecord[64];
	private uint _rawInputMode;
	private bool _entered;
	private string? _originalTitle;

	private ConsoleHost(nint input, nint output, uint inputMode, uint outputMode)
	{
		_input = input;
		_output = output;
		_inputMode = inputMode;
		_outputMode = outputMode;
		_outputCodePage = GetConsoleOutputCP();
		_stdout = Console.OpenStandardOutput();
		_timer = CreateWaitableTimerEx(0, null, HighResolutionTimer, TimerAllAccess);
		if (_timer == 0)
		{
			_timer = CreateWaitableTimerEx(0, null, 0, TimerAllAccess);
		}
	}

	/// <summary>The visible terminal size in character cells.</summary>
	public (int Columns, int Rows) Size => GetConsoleScreenBufferInfo(_output, out var info)
		? (info.WindowRight - info.WindowLeft + 1, info.WindowBottom - info.WindowTop + 1)
		: (80, 25);

	/// <summary>Opens the console of this process, or returns null when input or output is redirected.</summary>
	public static ConsoleHost? TryOpen()
	{
		var input = GetStdHandle(StandardInput);
		var output = GetStdHandle(StandardOutput);
		return GetConsoleMode(input, out var inputMode) && GetConsoleMode(output, out var outputMode)
			? new ConsoleHost(input, output, inputMode, outputMode)
			: null;
	}

	/// <summary>Switches to raw input and the alternate screen, with the cursor hidden and line wrapping off.</summary>
	public void Enter()
	{
		_ = SetConsoleOutputCP(Utf8CodePage);
		_ = SetConsoleMode(_output, _outputMode | EnableProcessedOutput | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn);
		// No line input, echo, quick edit or Ctrl+C signal; mouse events arrive as input (Shift + drag still selects text).
		_rawInputMode = EnableWindowInput | EnableExtendedFlags | EnableMouseInput;
		_ = SetConsoleMode(_input, _rawInputMode);
		_ = FlushConsoleInputBuffer(_input);
		Write("\u001b[?1049h\u001b[?25l\u001b[?7l"u8);
		_originalTitle = SetTitle("Pulse");
		_entered = true;
	}

	/// <summary>Restores the screen, cursor, modes and code page the console had before.</summary>
	public void Exit()
	{
		if (!_entered)
		{
			return;
		}

		_entered = false;
		Write("\u001b[?2026l\u001b[0m\u001b[?7h\u001b[?25h\u001b[?1049l"u8);
		if (_originalTitle is not null)
		{
			_ = SetTitle(_originalTitle);
		}

		_ = SetConsoleMode(_input, _inputMode);
		_ = SetConsoleMode(_output, _outputMode);
		_ = SetConsoleOutputCP(_outputCodePage);
	}

	/// <summary>Sets the tab title; returns the previous title, or null when it cannot be read.</summary>
	private static string? SetTitle(string title)
	{
		try
		{
			var previous = Console.Title;
			Console.Title = title;
			return previous;
		}
		catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
		{
			return null;
		}
	}

	public void Write(ReadOnlySpan<byte> bytes)
	{
		_stdout.Write(bytes);
		_stdout.Flush();
	}

	/// <summary>Adds every key press waiting in the input buffer to <paramref name="keys"/>, without blocking.</summary>
	public void ReadKeys(List<KeyPress> keys) => ReadInput(keys, null);

	/// <summary>Adds every key press and mouse event waiting in the input buffer, without blocking.</summary>
	public void ReadInput(List<KeyPress> keys, List<MouseInput>? mouse)
	{
		while (GetNumberOfConsoleInputEvents(_input, out var pending) && pending > 0)
		{
			uint read;
			fixed (InputRecord* buffer = _records)
			{
				if (!ReadConsoleInput(_input, buffer, (uint) _records.Length, out read))
				{
					return;
				}
			}

			for (var i = 0; i < read; i++)
			{
				ref readonly var record = ref _records[i];
				if (record.EventType == KeyEventType && record.KeyDown != 0)
				{
					keys.Add(new KeyPress(
						record.VirtualKeyCode,
						(char) record.UnicodeChar,
						(record.ControlKeyState & ShiftPressed) != 0,
						(record.ControlKeyState & ControlPressed) != 0,
						(record.ControlKeyState & AltPressed) != 0));
				}
				else if (record.EventType == MouseEventType && mouse is not null)
				{
					// For the wheel, the high word of the button state is the signed distance; one event is one step.
					var wheel = (record.MouseFlags & MouseWheeled) != 0 ? Math.Sign((short) (record.MouseButtons >> 16)) : 0;
					mouse.Add(new MouseInput(
						record.MouseColumn,
						record.MouseRow,
						(record.MouseButtons & LeftButtonPressed) != 0,
						(record.MouseFlags & MouseMoved) != 0,
						(record.MouseFlags & DoubleClicked) != 0,
						wheel,
						(record.MouseButtons & RightButtonPressed) != 0));
				}
			}
		}
	}

	/// <summary>
	/// Sends <paramref name="request"/> and collects the terminal's reply until <paramref name="isComplete"/>
	/// says it is all there, or the timeout passes. Replies arrive as input, so VT input is on meanwhile.
	/// </summary>
	public string Query(string request, Func<string, bool> isComplete, double timeoutSeconds)
	{
		_ = SetConsoleMode(_input, _rawInputMode | EnableVirtualTerminalInput);
		try
		{
			Write(Encoding.ASCII.GetBytes(request));
			var reply = new StringBuilder();
			var deadline = MonotonicClock.Now + timeoutSeconds;
			while (!isComplete(reply.ToString()))
			{
				var remaining = deadline - MonotonicClock.Now;
				if (remaining <= 0 || WaitForSingleObject(_input, (uint) Math.Ceiling(remaining * 1000)) != 0)
				{
					break;
				}

				uint read;
				fixed (InputRecord* buffer = _records)
				{
					if (!ReadConsoleInput(_input, buffer, (uint) _records.Length, out read))
					{
						break;
					}
				}

				for (var i = 0; i < read; i++)
				{
					if (_records[i].EventType == KeyEventType && _records[i].KeyDown != 0 && _records[i].UnicodeChar != 0)
					{
						reply.Append((char) _records[i].UnicodeChar);
					}
				}
			}

			return reply.ToString();
		}
		finally
		{
			_ = SetConsoleMode(_input, _rawInputMode);
		}
	}

	/// <summary>Sleeps up to <paramref name="seconds"/>, waking early when a key is pressed.</summary>
	public void Wait(double seconds)
	{
		if (seconds <= 0)
		{
			return;
		}

		if (_timer != 0)
		{
			var due = -(long) (seconds * 10_000_000); // negative: relative, in 100 ns units
			if (SetWaitableTimer(_timer, due, 0, 0, 0, false))
			{
				var handles = stackalloc nint[2] { _input, _timer };
				_ = WaitForMultipleObjects(2, handles, false, Infinite);
				return;
			}
		}

		_ = WaitForSingleObject(_input, (uint) Math.Ceiling(seconds * 1000));
	}

	public void Dispose()
	{
		Exit();
		if (_timer != 0)
		{
			_ = CloseHandle(_timer);
		}
	}

	[LibraryImport("kernel32.dll")]
	private static partial nint GetStdHandle(int handle);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetConsoleMode(nint handle, out uint mode);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleMode(nint handle, uint mode);

	[LibraryImport("kernel32.dll")]
	private static partial uint GetConsoleOutputCP();

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleOutputCP(uint codePage);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetConsoleScreenBufferInfo(nint output, out ConsoleScreenBufferInfo info);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetNumberOfConsoleInputEvents(nint input, out uint count);

	[LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleInputW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ReadConsoleInput(nint input, InputRecord* buffer, uint length, out uint read);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool FlushConsoleInputBuffer(nint input);

	[LibraryImport("kernel32.dll")]
	private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

	[LibraryImport("kernel32.dll")]
	private static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

	[LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint handle);

	/// <summary>INPUT_RECORD with its KEY_EVENT_RECORD (20 bytes).</summary>
	[StructLayout(LayoutKind.Explicit, Size = 20)]
	private struct InputRecord
	{
		[FieldOffset(0)] public ushort EventType;
		[FieldOffset(4)] public int KeyDown;
		[FieldOffset(8)] public ushort RepeatCount;
		[FieldOffset(10)] public ushort VirtualKeyCode;
		[FieldOffset(12)] public ushort VirtualScanCode;
		[FieldOffset(14)] public ushort UnicodeChar;
		[FieldOffset(16)] public uint ControlKeyState;

		// MOUSE_EVENT_RECORD shares the same bytes.
		[FieldOffset(4)] public short MouseColumn;
		[FieldOffset(6)] public short MouseRow;
		[FieldOffset(8)] public uint MouseButtons;
		[FieldOffset(16)] public uint MouseFlags;
	}

	/// <summary>CONSOLE_SCREEN_BUFFER_INFO (22 bytes).</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct ConsoleScreenBufferInfo
	{
		public short SizeX;
		public short SizeY;
		public short CursorX;
		public short CursorY;
		public ushort Attributes;
		public short WindowLeft;
		public short WindowTop;
		public short WindowRight;
		public short WindowBottom;
		public short MaximumWidth;
		public short MaximumHeight;
	}
}
