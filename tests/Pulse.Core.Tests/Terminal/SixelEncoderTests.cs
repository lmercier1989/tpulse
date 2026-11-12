using System.Text;

using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

public sealed class SixelEncoderTests
{
	[Fact]
	public void Encode_EmptyImage_SendsOnlyTheFrame()
	{
		// Arrange
		var pixels = new byte[4 * 12];

		// Act
		var sixel = Encode(pixels, 4, 12);

		// Assert
		Assert.StartsWith("\u001bP0;1;0q\"1;1;4;12", sixel);
		Assert.EndsWith("\u001b\\", sixel);
		Assert.DoesNotContain("#", sixel);
		Assert.Equal(1, sixel.Count(character => character == '-'));
	}

	[Fact]
	public void Encode_SinglePixel_StartsItsPassAtItsColumn()
	{
		// Arrange: register 5 at x = 1, y = 2.
		var pixels = new byte[3 * 6];
		pixels[(2 * 3) + 1] = 5;

		// Act
		var sixel = Encode(pixels, 3, 6);

		// Assert: one empty column, then a sixel with bit 2 set (63 + 4 = 'C').
		Assert.Contains("#5?C$", sixel);
	}

	[Fact]
	public void Encode_LongRun_UsesTheRepeatIntroducer()
	{
		// Arrange: the top row of a 20 x 6 image in register 7.
		var pixels = new byte[20 * 6];
		pixels.AsSpan(0, 20).Fill(7);

		// Act
		var sixel = Encode(pixels, 20, 6);

		// Assert
		Assert.Contains("#7!20@$", sixel);
	}

	[Fact]
	public void Encode_DecodesBackToTheSameImage()
	{
		// Arrange: a noisy image with partial last band and many registers, background mostly.
		const int width = 37;
		const int height = 23;
		var random = new Random(1234);
		var pixels = new byte[width * height];
		for (var i = 0; i < pixels.Length; i++)
		{
			pixels[i] = random.NextDouble() < 0.6 ? (byte) 0 : (byte) random.Next(1, ScopePalette.Instance.Count);
		}

		// Act
		var decoded = Decode(Encode(pixels, width, height), width, height);

		// Assert
		Assert.Equal(pixels, decoded);
	}

	[Fact]
	public void Encode_DefinesEveryUsedColourInPercent()
	{
		// Arrange
		var pixels = new byte[] { 0, 3, 0, 0, 0, 0 };
		var color = ScopePalette.Instance[3];

		// Act
		var sixel = Encode(pixels, 1, 6);

		// Assert
		Assert.Contains($"#3;2;{Percent(color.R)};{Percent(color.G)};{Percent(color.B)}", sixel);
	}

	[Fact]
	public void Encode_Opaque_PaintsTheBackgroundInThePanelColour()
	{
		// Arrange: one register-5 pixel on a background, 3 x 6.
		var pixels = new byte[3 * 6];
		pixels[(2 * 3) + 1] = 5;
		var background = ScopePalette.Instance[0];

		// Act
		var sixel = Encode(pixels, 3, 6, opaque: true);

		// Assert: register 0 is defined as the panel colour and covers every pixel but the drawn one.
		Assert.Contains($"#0;2;{Percent(background.R)};{Percent(background.G)};{Percent(background.B)}", sixel);
		Assert.Contains("#0~z~", sixel);
		Assert.Contains("#5?C", sixel);
	}

	[Fact]
	public void Encode_Opaque_DecodesToTheSameImage()
	{
		// Arrange
		var pixels = new byte[10 * 9];
		for (var i = 0; i < pixels.Length; i += 7)
		{
			pixels[i] = (byte) (1 + (i % 5));
		}

		// Act
		var decoded = Decode(Encode(pixels, 10, 9, opaque: true), 10, 9);

		// Assert
		Assert.Equal(pixels, decoded);
	}

	private static int Percent(byte channel) => (int) Math.Round(channel * 100 / 255.0);

	private static string Encode(byte[] pixels, int width, int height, bool opaque = false)
	{
		var output = new AnsiWriter();
		new SixelEncoder().Encode(pixels, width, height, ScopePalette.Instance, output, opaque);
		return Encoding.ASCII.GetString(output.Written);
	}

	/// <summary>A minimal Sixel decoder: registers, repeats, carriage return and line feed.</summary>
	private static byte[] Decode(string sixel, int width, int height)
	{
		var pixels = new byte[width * height];
		var start = sixel.IndexOf('q') + 1;
		var end = sixel.LastIndexOf('\u001b');
		int x = 0, band = 0, register = 0, repeat = 1;
		for (var i = start; i < end; i++)
		{
			var c = sixel[i];
			if (c == '"')
			{
				i = SkipNumbers(sixel, i + 1) - 1;
			}
			else if (c == '#')
			{
				var next = SkipNumbers(sixel, i + 1);
				register = int.Parse(sixel.AsSpan(i + 1).TrimStart('#')[..sixel.AsSpan(i + 1).IndexOfAnyExcept("0123456789")]);
				i = next - 1;
			}
			else if (c == '!')
			{
				var digits = sixel.AsSpan(i + 1);
				var length = digits.IndexOfAnyExcept("0123456789");
				repeat = int.Parse(digits[..length]);
				i += length;
			}
			else if (c == '$')
			{
				x = 0;
			}
			else if (c == '-')
			{
				x = 0;
				band += 6;
			}
			else if (c is >= '?' and <= '~')
			{
				var bits = c - 63;
				for (var r = 0; r < repeat; r++, x++)
				{
					for (var bit = 0; bit < 6; bit++)
					{
						if ((bits & (1 << bit)) != 0 && band + bit < height && x < width)
						{
							pixels[((band + bit) * width) + x] = (byte) register;
						}
					}
				}

				repeat = 1;
			}
		}

		return pixels;
	}

	/// <summary>Skips a list of numbers separated by semicolons; returns the index after it.</summary>
	private static int SkipNumbers(string text, int index)
	{
		while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == ';'))
		{
			index++;
		}

		return index;
	}
}
