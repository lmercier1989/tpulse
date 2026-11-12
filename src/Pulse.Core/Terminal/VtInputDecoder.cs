using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Core.Terminal;

/// <summary>A key press decoded from terminal input, with the Windows virtual-key code the monitor's key handling uses.</summary>
/// <param name="VirtualKey">The Windows virtual-key code: 'A' to 'Z', '0' to '9', 0x25 for the left arrow, 0x70 for F1, ...</param>
/// <param name="Character">The character typed; 0 for a key without one (arrows, function keys).</param>
public readonly record struct VtKey(ushort VirtualKey, char Character, bool Shift, bool Control, bool Alt);

/// <summary>A mouse event decoded from terminal input, in character cells from 0.</summary>
/// <param name="LeftButton">The left button is down after this event.</param>
/// <param name="Moved">The mouse moved (rather than a button changing).</param>
/// <param name="DoubleClick">The second press of the left button within half a second on the same cell.</param>
/// <param name="Wheel">Wheel steps: positive away from the user, negative toward; 0 for no wheel.</param>
/// <param name="RightButton">The right button is down after this event.</param>
public readonly record struct VtMouse(int Column, int Row, bool LeftButton, bool Moved, bool DoubleClick, int Wheel, bool RightButton = false);

/// <summary>
/// Turns the bytes a Unix terminal sends in raw mode into key presses and mouse events, as the Windows console reports
/// them: UTF-8 characters, control characters (Ctrl+C is the character 3), escape sequences for the arrows, Home, End and
/// the function keys (CSI and SS3, with xterm modifiers), Alt as an escape before the key, and SGR mouse reports
/// (<c>CSI &lt; b ; x ; y M</c>). Replies to terminal queries that arrive late and other sequences are dropped. A sequence
/// cut between two reads is kept until the rest arrives, or given up after <see cref="StaleSeconds"/>.
/// </summary>
public sealed class VtInputDecoder
{
	/// <summary>An incomplete sequence older than this was a lone Escape (or garbage) after all.</summary>
	public const double StaleSeconds = 0.1;

	private const double DoubleClickSeconds = 0.5;
	private const byte Escape = 0x1B;
	private const int Incomplete = -1;
	private const int Invalid = -2;

	// Windows virtual-key codes.
	private const ushort BackspaceKey = 0x08;
	private const ushort TabKey = 0x09;
	private const ushort EnterKey = 0x0D;
	private const ushort EscapeKey = 0x1B;
	private const ushort PageUpKey = 0x21;
	private const ushort PageDownKey = 0x22;
	private const ushort EndKey = 0x23;
	private const ushort HomeKey = 0x24;
	private const ushort LeftKey = 0x25;
	private const ushort UpKey = 0x26;
	private const ushort RightKey = 0x27;
	private const ushort DownKey = 0x28;
	private const ushort InsertKey = 0x2D;
	private const ushort DeleteKey = 0x2E;
	private const ushort F1Key = 0x70;
	private const ushort MenuKey = 0x5D;

	/// <summary>The keys above the digits on a US keyboard: Shift+1 arrives as '!', and means Shift+1 to the monitor.</summary>
	private const string ShiftedDigits = ")!@#$%^&*(";

	private readonly List<byte> _pending = [];
	private double _pendingSince;
	private bool _leftDown;
	private bool _rightDown;
	private double _lastPress = double.NegativeInfinity;
	private (int Column, int Row) _lastPressCell = (-1, -1);

	/// <summary>Bytes of an incomplete sequence wait for the rest; decode again (with no input) to give them up when stale.</summary>
	public bool HasPending => _pending.Count > 0;

	/// <summary>
	/// Decodes <paramref name="input"/> (what was read just now) and any bytes kept from before, at time
	/// <paramref name="now"/> in seconds. Mouse events are dropped when <paramref name="mouse"/> is null.
	/// </summary>
	public void Decode(ReadOnlySpan<byte> input, List<VtKey> keys, List<VtMouse>? mouse, double now)
	{
		if (_pending.Count == 0)
		{
			_pendingSince = now;
		}

		_pending.AddRange(input);
		if (_pending.Count == 0)
		{
			return;
		}

		var used = Decode(CollectionsMarshal.AsSpan(_pending), keys, mouse, now, flush: now - _pendingSince >= StaleSeconds);
		_pending.RemoveRange(0, used);
		if (_pending.Count > 0 && used > 0)
		{
			_pendingSince = now;
		}
	}

	/// <summary>Decodes what it can; returns how many bytes were used. With <paramref name="flush"/>, incomplete input is taken as it is.</summary>
	private int Decode(ReadOnlySpan<byte> bytes, List<VtKey> keys, List<VtMouse>? mouse, double now, bool flush)
	{
		var i = 0;
		while (i < bytes.Length)
		{
			var b = bytes[i];
			if (b == Escape)
			{
				if (i + 1 == bytes.Length)
				{
					// Everything the terminal had was read at once, so an escape at the end is the Escape key itself.
					keys.Add(new VtKey(EscapeKey, (char) Escape, false, false, false));
					i++;
					continue;
				}

				var next = bytes[i + 1];
				if (next == '[')
				{
					var end = FinalByte(bytes, i + 2);
					if (end == Incomplete && !flush)
					{
						return i;
					}

					if (end < 0)
					{
						// Cut off, or not a control sequence: the escape was a key of its own.
						keys.Add(new VtKey(EscapeKey, (char) Escape, false, false, false));
						i++;
						continue;
					}

					ControlSequence(bytes[(i + 2)..end], (char) bytes[end], keys, mouse, now);
					i = end + 1;
				}
				else if (next == 'O')
				{
					if (i + 2 >= bytes.Length)
					{
						if (!flush)
						{
							return i;
						}

						keys.Add(new VtKey(EscapeKey, (char) Escape, false, false, false));
						i++;
						continue;
					}

					SingleShift((char) bytes[i + 2], keys);
					i += 3;
				}
				else if (next == Escape)
				{
					keys.Add(new VtKey(EscapeKey, (char) Escape, false, false, false));
					i++;
				}
				else
				{
					// Alt+key: the terminal sends an escape before the key.
					var used = Character(bytes[(i + 1)..], keys, alt: true, flush);
					if (used == 0)
					{
						return i;
					}

					i += 1 + used;
				}
			}
			else
			{
				var used = Character(bytes[i..], keys, alt: false, flush);
				if (used == 0)
				{
					return i;
				}

				i += used;
			}
		}

		return i;
	}

	/// <summary>One character (a control character or UTF-8); returns the bytes used, 0 when the character is incomplete.</summary>
	private static int Character(ReadOnlySpan<byte> bytes, List<VtKey> keys, bool alt, bool flush)
	{
		var b = bytes[0];
		switch (b)
		{
			case 0x7F or 0x08:
				keys.Add(new VtKey(BackspaceKey, '\b', false, false, alt));
				return 1;
			case 0x09:
				keys.Add(new VtKey(TabKey, '\t', false, false, alt));
				return 1;
			case 0x0D or 0x0A:
				keys.Add(new VtKey(EnterKey, '\r', false, false, alt));
				return 1;
			case >= 0x01 and <= 0x1A:
				// Ctrl+A to Ctrl+Z; the character stays the control character, so Ctrl+C reads as character 3.
				keys.Add(new VtKey((ushort) ('A' + b - 1), (char) b, false, true, alt));
				return 1;
			case < 0x20:
				return 1; // Ctrl+Space, Ctrl+\ and the like: nothing the monitor uses.
		}

		var length = b switch
		{
			< 0x80 => 1,
			>= 0xF0 => 4,
			>= 0xE0 => 3,
			>= 0xC0 => 2,
			_ => 1,
		};
		if (bytes.Length < length)
		{
			return flush ? bytes.Length : 0;
		}

		Span<char> chars = stackalloc char[2];
		var count = Encoding.UTF8.GetChars(bytes[..length], chars);
		if (count == 1)
		{
			keys.Add(Printable(chars[0], alt));
		}

		return length;
	}

	/// <summary>A printable character with the virtual key a Windows keyboard gives it.</summary>
	private static VtKey Printable(char c, bool alt)
	{
		if (char.IsAsciiLetterLower(c))
		{
			return new VtKey((ushort) char.ToUpperInvariant(c), c, false, false, alt);
		}

		if (char.IsAsciiLetterUpper(c))
		{
			return new VtKey(c, c, true, false, alt);
		}

		if (char.IsAsciiDigit(c) || c == ' ')
		{
			return new VtKey(c, c, false, false, alt);
		}

		var digit = ShiftedDigits.IndexOf(c, StringComparison.Ordinal);
		return digit >= 0
			? new VtKey((ushort) ('0' + digit), c, true, false, alt)
			: new VtKey(0, c, false, false, alt);
	}

	/// <summary>
	/// The index of the final byte of a control sequence whose parameters start at <paramref name="from"/>;
	/// <see cref="Incomplete"/> when the input ends first, <see cref="Invalid"/> when a byte cannot be part of one.
	/// </summary>
	private static int FinalByte(ReadOnlySpan<byte> bytes, int from)
	{
		for (var i = from; i < bytes.Length; i++)
		{
			switch (bytes[i])
			{
				case >= 0x40 and <= 0x7E:
					return i;
				case < 0x20 or > 0x7E:
					return Invalid;
			}
		}

		return Incomplete;
	}

	/// <summary>A CSI sequence: <paramref name="parameters"/> between "ESC [" and the final character.</summary>
	private void ControlSequence(ReadOnlySpan<byte> parameters, char final, List<VtKey> keys, List<VtMouse>? mouse, double now)
	{
		if (!parameters.IsEmpty && parameters[0] == '<')
		{
			if (final is 'M' or 'm' && mouse is not null)
			{
				Mouse(parameters[1..], final == 'M', mouse, now);
			}

			return;
		}

		if (!parameters.IsEmpty && parameters[0] is (byte) '?' or (byte) '>' or (byte) '=')
		{
			return; // A reply to a query (device attributes, modes).
		}

		Span<int> numbers = stackalloc int[4];
		var count = Numbers(parameters, numbers);
		var modifiers = count >= 2 ? Math.Max(0, numbers[1] - 1) : 0;
		switch (final)
		{
			case 'A': Add(UpKey); break;
			case 'B': Add(DownKey); break;
			case 'C': Add(RightKey); break;
			case 'D': Add(LeftKey); break;
			case 'H': Add(HomeKey); break;
			case 'F': Add(EndKey); break;
			case 'Z': keys.Add(new VtKey(TabKey, '\t', true, false, false)); break;
			case 'P' or 'Q' or 'R' or 'S' when count == 0 || numbers[0] == 1:
				Add((ushort) (F1Key + final - 'P'));
				break;
			case '~' when count >= 1:
				var key = numbers[0] switch
				{
					1 or 7 => HomeKey,
					2 => InsertKey,
					3 => DeleteKey,
					4 or 8 => EndKey,
					5 => PageUpKey,
					6 => PageDownKey,
					>= 11 and <= 15 => (ushort) (F1Key + numbers[0] - 11),
					>= 17 and <= 21 => (ushort) (F1Key + 5 + numbers[0] - 17),
					23 or 24 => (ushort) (F1Key + 10 + numbers[0] - 23),
					29 => MenuKey,
					_ => (ushort) 0,
				};
				if (key != 0)
				{
					Add(key);
				}

				break;
		}

		void Add(ushort virtualKey)
			=> keys.Add(new VtKey(virtualKey, '\0', (modifiers & 1) != 0, (modifiers & 4) != 0, (modifiers & 2) != 0));
	}

	/// <summary>SS3 (ESC O x): the arrows, Home and End in application cursor mode, and F1 to F4.</summary>
	private static void SingleShift(char final, List<VtKey> keys)
	{
		ushort key = final switch
		{
			'A' => UpKey,
			'B' => DownKey,
			'C' => RightKey,
			'D' => LeftKey,
			'H' => HomeKey,
			'F' => EndKey,
			'M' => EnterKey,
			>= 'P' and <= 'S' => (ushort) (F1Key + final - 'P'),
			_ => 0,
		};
		if (key != 0)
		{
			keys.Add(new VtKey(key, key == EnterKey ? '\r' : '\0', false, false, false));
		}
	}

	/// <summary>An SGR mouse report: button and modifier bits, column, row (from 1); M for a press or motion, m for a release.</summary>
	private void Mouse(ReadOnlySpan<byte> parameters, bool press, List<VtMouse> mouse, double now)
	{
		Span<int> numbers = stackalloc int[3];
		if (Numbers(parameters, numbers) < 3)
		{
			return;
		}

		var (code, column, row) = (numbers[0], numbers[1] - 1, numbers[2] - 1);
		var button = code & 3; // 0 left, 1 middle, 2 right, 3 none (a motion without a button)
		if ((code & 64) != 0)
		{
			if (press && button is 0 or 1)
			{
				mouse.Add(new VtMouse(column, row, _leftDown, false, false, button == 0 ? 1 : -1, _rightDown));
			}

			return;
		}

		if ((code & 32) != 0)
		{
			mouse.Add(new VtMouse(column, row, button == 0, true, false, 0, button == 2));
			return;
		}

		if (button == 2)
		{
			_rightDown = press;
			mouse.Add(new VtMouse(column, row, _leftDown, false, false, 0, press));
			return;
		}

		if (button != 0)
		{
			return; // The middle button does nothing in the monitor.
		}

		var doubleClick = false;
		if (press)
		{
			doubleClick = now - _lastPress <= DoubleClickSeconds && _lastPressCell == (column, row);
			_lastPress = doubleClick ? double.NegativeInfinity : now;
			_lastPressCell = (column, row);
		}

		_leftDown = press;
		mouse.Add(new VtMouse(column, row, press, false, doubleClick, 0, _rightDown));
	}

	/// <summary>The numbers of "1;5" into <paramref name="numbers"/>; returns how many there were (empty ones count as 0).</summary>
	private static int Numbers(ReadOnlySpan<byte> text, Span<int> numbers)
	{
		if (text.IsEmpty)
		{
			return 0;
		}

		var count = 0;
		var value = 0;
		foreach (var b in text)
		{
			if (b == ';' || b == ':')
			{
				if (count < numbers.Length)
				{
					numbers[count] = value;
				}

				count++;
				value = 0;
			}
			else if (b is >= (byte) '0' and <= (byte) '9')
			{
				value = Math.Min(100_000, (value * 10) + (b - '0'));
			}
		}

		if (count < numbers.Length)
		{
			numbers[count] = value;
		}

		return Math.Min(count + 1, numbers.Length);
	}
}
