using System.Globalization;
using System.Text;

namespace Pulse.Core.Terminal;

/// <summary>
/// A reusable byte buffer for one terminal frame: escape sequences, UTF-8 text and Sixel data are appended
/// and the frame is written to the console in one call. Positions are 0-based.
/// </summary>
public sealed class AnsiWriter
{
	private byte[] _buffer = new byte[64 * 1024];
	private int _length;

	public int Length => _length;

	public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

	public void Clear() => _length = 0;

	public AnsiWriter Byte(byte value)
	{
		Ensure(1);
		_buffer[_length++] = value;
		return this;
	}

	public AnsiWriter Byte(char value) => Byte((byte) value);

	public AnsiWriter Raw(ReadOnlySpan<byte> bytes)
	{
		Ensure(bytes.Length);
		bytes.CopyTo(_buffer.AsSpan(_length));
		_length += bytes.Length;
		return this;
	}

	/// <summary>
	/// Blanks <paramref name="count"/> cells from the cursor with spaces in the current background. Unlike erasing
	/// (<c>ECH</c>), writing over a Sixel image keeps the rest of it: Konsole deletes a whole image when any of its cells
	/// is erased.
	/// </summary>
	public AnsiWriter Blank(int count)
	{
		if (count <= 0)
		{
			return this;
		}

		Ensure(count);
		_buffer.AsSpan(_length, count).Fill((byte) ' ');
		_length += count;
		return this;
	}

	/// <summary>Appends ASCII text (escape sequences, Sixel data).</summary>
	public AnsiWriter Ascii(string text)
	{
		Ensure(text.Length);
		foreach (var character in text)
		{
			_buffer[_length++] = (byte) character;
		}

		return this;
	}

	public AnsiWriter Number(int value)
	{
		Ensure(11);
		value.TryFormat(_buffer.AsSpan(_length), out var written, provider: CultureInfo.InvariantCulture);
		_length += written;
		return this;
	}

	/// <summary>Appends text as UTF-8.</summary>
	public AnsiWriter Text(ReadOnlySpan<char> text)
	{
		Ensure(Encoding.UTF8.GetMaxByteCount(text.Length));
		_length += Encoding.UTF8.GetBytes(text, _buffer.AsSpan(_length));
		return this;
	}

	public AnsiWriter MoveTo(int row, int column) => Ascii("\u001b[").Number(row + 1).Byte(';').Number(column + 1).Byte('H');

	public AnsiWriter Foreground(Rgb color) => Ascii("\u001b[38;2;").Number(color.R).Byte(';').Number(color.G).Byte(';').Number(color.B).Byte('m');

	public AnsiWriter Background(Rgb color) => Ascii("\u001b[48;2;").Number(color.R).Byte(';').Number(color.G).Byte(';').Number(color.B).Byte('m');

	public AnsiWriter Bold() => Ascii("\u001b[1m");

	public AnsiWriter NotBold() => Ascii("\u001b[22m");

	/// <summary>Erases the whole line in the current background colour.</summary>
	public AnsiWriter EraseLine() => Ascii("\u001b[2K");

	private void Ensure(int extra)
	{
		if (_length + extra > _buffer.Length)
		{
			Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
		}
	}
}
