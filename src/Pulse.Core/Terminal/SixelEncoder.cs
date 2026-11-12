namespace Pulse.Core.Terminal;

/// <summary>
/// Encodes a palette image as Sixel (DEC graphics, supported by Windows Terminal 1.22+). The image is cut into
/// bands of six pixel rows; per band every colour is one pass of characters whose six bits say which rows have
/// that colour. Runs are compressed with the repeat introducer (<c>!</c>), and a colour pass starts at its first
/// pixel and stops at its last.
/// <para>
/// Register 0 is the background and is never encoded: the image is transparent there (background select 1),
/// so the cells behind it show, which the caller erases to the panel colour first. Terminals fill an opaque
/// image background with their own default colour rather than the panel colour, and a scope is mostly
/// background, so this is both correct and cheap. Allocation-free once its buffers have grown.
/// </para>
/// </summary>
public sealed class SixelEncoder
{
	private const int Registers = 256;
	private const byte BackgroundRegister = 0;
	private const byte EmptySixel = (byte) '?';

	private readonly byte[][] _passes = new byte[Registers][];
	private readonly int[] _passLength = new int[Registers];
	private readonly int[] _nextX = new int[Registers];
	private readonly byte[] _runCharacter = new byte[Registers];
	private readonly int[] _runLength = new int[Registers];
	private readonly bool[] _active = new bool[Registers];
	private readonly byte[] _activeOrder = new byte[Registers];
	private readonly byte[] _columnMask = new byte[Registers];
	private int _activeCount;

	/// <summary>Appends the image as one Sixel sequence, drawn at the cursor with square pixels.</summary>
	public void Encode(ReadOnlySpan<byte> pixels, int width, int height, ScopePalette palette, AnsiWriter output)
	{
		// P2 = 1: pixels that are not drawn stay transparent.
		output.Ascii("\u001bP0;1;0q\"1;1;").Number(width).Byte(';').Number(height);

		Span<bool> used = stackalloc bool[Registers];
		foreach (var register in pixels)
		{
			used[register] = true;
		}

		for (var register = 1; register < palette.Count; register++)
		{
			if (used[register])
			{
				Define(output, palette, register);
			}
		}

		Span<byte> columnRegisters = stackalloc byte[6];
		for (var top = 0; top < height; top += 6)
		{
			var rows = Math.Min(6, height - top);
			_activeCount = 0;
			for (var x = 0; x < width; x++)
			{
				var count = 0;
				for (var row = 0; row < rows; row++)
				{
					var register = pixels[((top + row) * width) + x];
					if (register == BackgroundRegister)
					{
						continue;
					}

					if (_columnMask[register] == 0)
					{
						columnRegisters[count++] = register;
					}

					_columnMask[register] |= (byte) (1 << row);
				}

				for (var k = 0; k < count; k++)
				{
					var register = columnRegisters[k];
					Put(register, x, _columnMask[register]);
					_columnMask[register] = 0;
				}
			}

			for (var k = 0; k < _activeCount; k++)
			{
				var register = _activeOrder[k];
				Flush(register);
				output.Byte('#').Number(register).Raw(_passes[register].AsSpan(0, _passLength[register])).Byte('$');
				_active[register] = false;
			}

			if (top + 6 < height)
			{
				output.Byte('-');
			}
		}

		output.Ascii("\u001b\\");
	}

	private static int Percent(byte channel) => (int) Math.Round(channel * 100 / 255.0);

	private static void Define(AnsiWriter output, ScopePalette palette, int register)
	{
		var color = palette[register];
		output.Byte('#').Number(register).Ascii(";2;")
			.Number(Percent(color.R)).Byte(';').Number(Percent(color.G)).Byte(';').Number(Percent(color.B));
	}

	private void Put(byte register, int x, byte mask)
	{
		if (!_active[register])
		{
			_active[register] = true;
			_activeOrder[_activeCount++] = register;
			_nextX[register] = 0;
			_runLength[register] = 0;
			_passLength[register] = 0;
			_passes[register] ??= new byte[512];
		}

		if (x > _nextX[register])
		{
			Run(register, EmptySixel, x - _nextX[register]);
		}

		Run(register, (byte) (63 + mask), 1);
		_nextX[register] = x + 1;
	}

	private void Run(byte register, byte character, int count)
	{
		if (_runLength[register] > 0 && _runCharacter[register] == character)
		{
			_runLength[register] += count;
			return;
		}

		Flush(register);
		_runCharacter[register] = character;
		_runLength[register] = count;
	}

	private void Flush(byte register)
	{
		var count = _runLength[register];
		if (count == 0)
		{
			return;
		}

		var character = _runCharacter[register];
		if (count >= 4)
		{
			Append(register, (byte) '!');
			Span<byte> digits = stackalloc byte[11];
			count.TryFormat(digits, out var written, provider: System.Globalization.CultureInfo.InvariantCulture);
			foreach (var digit in digits[..written])
			{
				Append(register, digit);
			}

			Append(register, character);
		}
		else
		{
			for (var i = 0; i < count; i++)
			{
				Append(register, character);
			}
		}

		_runLength[register] = 0;
	}

	private void Append(byte register, byte value)
	{
		ref var pass = ref _passes[register];
		if (_passLength[register] == pass.Length)
		{
			Array.Resize(ref pass, pass.Length * 2);
		}

		pass[_passLength[register]++] = value;
	}
}
