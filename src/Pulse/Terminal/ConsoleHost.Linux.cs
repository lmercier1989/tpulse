using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Pulse.Core.Diagnostics;
using Pulse.Core.Terminal;

namespace Pulse.Terminal;

/// <summary>
/// The Unix terminal the monitor runs in: raw input through termios (no echo, no line editing, Ctrl+C as a key), the
/// alternate screen, mouse reports (SGR, every motion), terminal queries, and a wait that wakes on input or a resize, so
/// frames are paced without a thread or a busy loop. The bytes the terminal sends are decoded into the same key presses
/// and mouse events the Windows console gives. Everything is restored on <see cref="Exit"/>, also when the process is
/// asked to stop (SIGTERM, SIGHUP).
/// </summary>
internal sealed unsafe partial class ConsoleHost : IDisposable
{
	private const int StandardInput = 0;
	private const int StandardOutput = 1;
	private const int TcsaNow = 0;
	private const int TcsaFlush = 2;
	private const ulong TiocGetWindowSize = 0x5413;
	private const short PollIn = 0x1;
	private const int Interrupted = 4;

	private static readonly byte[] EnterSequence = Encoding.ASCII.GetBytes(
		"\u001b[?1049h\u001b[?25l\u001b[?7l" // alternate screen, no cursor, no line wrapping
		+ "\u001b[?1003h\u001b[?1006h" // every mouse motion, reported as SGR
		+ "\u001b[22;0t\u001b]0;Pulse\u0007"); // keep the title, then set ours

	private static readonly byte[] ExitSequence = Encoding.ASCII.GetBytes(
		"\u001b[?1003l\u001b[?1006l\u001b[?2026l\u001b[0m\u001b[?7h\u001b[?25h\u001b[?1049l\u001b[23;0t");

	private readonly Termios _original;
	private readonly Stream _stdout;
	private readonly VtInputDecoder _decoder = new();
	private readonly List<VtKey> _keys = [];
	private readonly List<VtMouse> _mouse = [];
	private readonly byte[] _read = new byte[4096];
	private readonly int _wakeRead = -1;
	private readonly int _wakeWrite = -1;
	private readonly List<PosixSignalRegistration> _signals = [];
	private readonly Lock _lock = new();
	private bool _entered;
	private bool _closed;

	private ConsoleHost(Termios original)
	{
		_original = original;
		_stdout = Console.OpenStandardOutput();
		var pipe = stackalloc int[2];
		if (Pipe(pipe) == 0)
		{
			_wakeRead = pipe[0];
			_wakeWrite = pipe[1];
		}
	}

	/// <summary>The visible terminal size in character cells.</summary>
	public (int Columns, int Rows) Size
	{
		get
		{
			WindowSize size;
			return IoControl(StandardOutput, TiocGetWindowSize, &size) == 0 && size.Columns > 0 && size.Rows > 0
				? (size.Columns, size.Rows)
				: (80, 25);
		}
	}

	/// <summary>Opens the terminal of this process, or returns null when input or output is not a terminal.</summary>
	public static ConsoleHost? TryOpen()
	{
		if (Console.IsInputRedirected || Console.IsOutputRedirected)
		{
			return null;
		}

		Termios original;
		return GetAttributes(StandardInput, &original) == 0 ? new ConsoleHost(original) : null;
	}

	/// <summary>Switches to raw input and the alternate screen, with the cursor hidden, line wrapping off and mouse reports on.</summary>
	public void Enter()
	{
		lock (_lock)
		{
			MakeRaw(TcsaFlush);
			Write(EnterSequence);
			_entered = true;
		}

		foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
		{
			_signals.Add(PosixSignalRegistration.Create(signal, _ => Exit()));
		}

		_signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context =>
		{
			context.Cancel = true;
			Wake();
		}));
		_signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGCONT, _ =>
		{
			// Back from a stop: the shell may have put the terminal back into line mode meanwhile.
			lock (_lock)
			{
				if (_entered)
				{
					MakeRaw(TcsaNow);
				}
			}

			Wake();
		}));
	}

	/// <summary>Restores the screen, cursor, mouse, title and terminal modes as they were.</summary>
	public void Exit()
	{
		lock (_lock)
		{
			if (!_entered)
			{
				return;
			}

			_entered = false;
			try
			{
				Write(ExitSequence);
			}
			catch (IOException)
			{
				// The terminal is gone (SIGHUP); there is nothing left to restore on screen.
			}

			var original = _original;
			_ = SetAttributes(StandardInput, TcsaNow, &original);
		}
	}

	public void Write(ReadOnlySpan<byte> bytes)
	{
		_stdout.Write(bytes);
		_stdout.Flush();
	}

	/// <summary>Adds every key press waiting in the input to <paramref name="keys"/>, without blocking.</summary>
	public void ReadKeys(List<KeyPress> keys) => ReadInput(keys, null);

	/// <summary>Adds every key press and mouse event waiting in the input, without blocking.</summary>
	public void ReadInput(List<KeyPress> keys, List<MouseInput>? mouse)
	{
		_keys.Clear();
		_mouse.Clear();
		var now = MonotonicClock.Now;
		while (!_closed && Poll(0) is var ready && (ready & 1) != 0)
		{
			var read = Read(_read);
			if (read <= 0)
			{
				// The terminal closed: quit as if Ctrl+C was pressed.
				_closed = true;
				_keys.Add(new VtKey('C', '\u0003', false, true, false));
				break;
			}

			_decoder.Decode(_read.AsSpan(0, read), _keys, mouse is null ? null : _mouse, now);
		}

		if (_decoder.HasPending)
		{
			_decoder.Decode([], _keys, mouse is null ? null : _mouse, now);
		}

		foreach (var key in _keys)
		{
			keys.Add(new KeyPress(key.VirtualKey, key.Character, key.Shift, key.Control, key.Alt));
		}

		if (mouse is not null)
		{
			foreach (var item in _mouse)
			{
				mouse.Add(new MouseInput(item.Column, item.Row, item.LeftButton, item.Moved, item.DoubleClick, item.Wheel, item.RightButton));
			}
		}
	}

	/// <summary>
	/// Sends <paramref name="request"/> and collects the terminal's reply until <paramref name="isComplete"/> says it is all
	/// there, or the timeout passes.
	/// </summary>
	public string Query(string request, Func<string, bool> isComplete, double timeoutSeconds)
	{
		Write(Encoding.ASCII.GetBytes(request));
		var reply = new StringBuilder();
		var deadline = MonotonicClock.Now + timeoutSeconds;
		while (!isComplete(reply.ToString()))
		{
			var remaining = deadline - MonotonicClock.Now;
			if (remaining <= 0 || (Poll(remaining) & 1) == 0)
			{
				break;
			}

			var read = Read(_read);
			if (read <= 0)
			{
				break;
			}

			reply.Append(Encoding.Latin1.GetString(_read, 0, read));
		}

		return reply.ToString();
	}

	/// <summary>Sleeps up to <paramref name="seconds"/>, waking early on input or when the terminal is resized.</summary>
	public void Wait(double seconds)
	{
		if (seconds <= 0)
		{
			return;
		}

		if ((Poll(seconds) & 2) != 0)
		{
			var drain = stackalloc byte[64];
			_ = ReadFile(_wakeRead, drain, 64);
		}
	}

	public void Dispose()
	{
		foreach (var signal in _signals)
		{
			signal.Dispose();
		}

		Exit();
		if (_wakeRead >= 0)
		{
			_ = Close(_wakeRead);
			_ = Close(_wakeWrite);
		}
	}

	/// <summary>Raw input like cfmakeraw, but with the output processing left as it was (a newline still returns the carriage).</summary>
	private void MakeRaw(int when)
	{
		var raw = _original;
		MakeRaw(&raw);
		raw.OutputFlags = _original.OutputFlags;
		if (SetAttributes(StandardInput, when, &raw) != 0)
		{
			Log.WarnOnce("terminal-raw", $"The terminal could not be switched to raw input (errno {Marshal.GetLastPInvokeError()}).");
		}
	}

	private void Wake()
	{
		if (_wakeWrite >= 0)
		{
			byte one = 1;
			_ = WriteFile(_wakeWrite, &one, 1);
		}
	}

	/// <summary>Waits for input or a wake-up; bit 1 is set for input, bit 2 for a wake-up. Retries when a signal interrupts.</summary>
	private int Poll(double seconds)
	{
		var fds = stackalloc PollDescriptor[2];
		fds[0] = new PollDescriptor { Descriptor = StandardInput, Events = PollIn };
		fds[1] = new PollDescriptor { Descriptor = _wakeRead, Events = PollIn };
		var count = _wakeRead >= 0 ? 2u : 1u;
		var whole = Math.Floor(seconds);
		var timeout = new TimeSpec { Seconds = (long) whole, Nanoseconds = (long) ((seconds - whole) * 1e9) };
		int result;
		do
		{
			result = PollWait(fds, count, &timeout, null);
		}
		while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);

		if (result <= 0)
		{
			return 0;
		}

		// Input, or a hang-up (then a read returns 0 and the monitor quits).
		return (fds[0].Returned != 0 ? 1 : 0) | (count == 2 && fds[1].Returned != 0 ? 2 : 0);
	}

	private int Read(byte[] buffer)
	{
		fixed (byte* target = buffer)
		{
			nint read;
			do
			{
				read = ReadFile(StandardInput, target, buffer.Length);
			}
			while (read < 0 && Marshal.GetLastPInvokeError() == Interrupted);

			return (int) read;
		}
	}

	[LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
	private static partial int GetAttributes(int descriptor, Termios* termios);

	[LibraryImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
	private static partial int SetAttributes(int descriptor, int when, Termios* termios);

	[LibraryImport("libc", EntryPoint = "cfmakeraw")]
	private static partial void MakeRaw(Termios* termios);

	[LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
	private static partial int IoControl(int descriptor, ulong request, WindowSize* size);

	[LibraryImport("libc", EntryPoint = "ppoll", SetLastError = true)]
	private static partial int PollWait(PollDescriptor* descriptors, nuint count, TimeSpec* timeout, void* signalMask);

	[LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
	private static partial nint ReadFile(int descriptor, byte* buffer, nint count);

	[LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
	private static partial nint WriteFile(int descriptor, byte* buffer, nint count);

	[LibraryImport("libc", EntryPoint = "pipe2", SetLastError = true)]
	private static partial int Pipe2(int* descriptors, int flags);

	[LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
	private static partial int Close(int descriptor);

	/// <summary>A pipe whose ends are not inherited by child processes (git, journalctl) and never block.</summary>
	private static int Pipe(int* descriptors)
	{
		const int CloseOnExec = 0x80000;
		const int NonBlocking = 0x800;
		return Pipe2(descriptors, CloseOnExec | NonBlocking);
	}

	/// <summary>struct termios of glibc and musl (60 bytes; the flags come first, the control characters after).</summary>
	[StructLayout(LayoutKind.Sequential, Size = 64)]
	private struct Termios
	{
		public uint InputFlags;
		public uint OutputFlags;
		public uint ControlFlags;
		public uint LocalFlags;
	}

	/// <summary>struct winsize.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct WindowSize
	{
		public ushort Rows;
		public ushort Columns;
		public ushort PixelWidth;
		public ushort PixelHeight;
	}

	/// <summary>struct pollfd.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct PollDescriptor
	{
		public int Descriptor;
		public short Events;
		public short Returned;
	}

	/// <summary>struct timespec on 64-bit Linux.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct TimeSpec
	{
		public long Seconds;
		public long Nanoseconds;
	}
}
