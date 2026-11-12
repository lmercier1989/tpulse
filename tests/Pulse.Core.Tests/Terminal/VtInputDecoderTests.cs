using System.Text;

using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

/// <summary>What a Unix terminal sends in raw mode, decoded into the keys and mouse events the monitor handles.</summary>
public sealed class VtInputDecoderTests
{
	private readonly VtInputDecoder _decoder = new();
	private readonly List<VtKey> _keys = [];
	private readonly List<VtMouse> _mouse = [];

	[Fact]
	public void Letters_GetTheirVirtualKey_AndShiftForCapitals()
	{
		Decode("qH");
		Assert.Equal([new VtKey('Q', 'q', false, false, false), new VtKey('H', 'H', true, false, false)], _keys);
	}

	[Fact]
	public void ShiftedDigit_IsTheDigitWithShift()
	{
		Decode("3#");
		Assert.Equal([new VtKey('3', '3', false, false, false), new VtKey('3', '#', true, false, false)], _keys);
	}

	[Fact]
	public void CtrlC_IsCharacterThree()
	{
		Decode("\u0003");
		Assert.Equal(new VtKey('C', '\u0003', false, true, false), Assert.Single(_keys));
	}

	[Theory]
	[InlineData("\u001b[D", 0x25, false)]
	[InlineData("\u001bOC", 0x27, false)]
	[InlineData("\u001b[1;2D", 0x25, true)]
	[InlineData("\u001b[H", 0x24, false)]
	[InlineData("\u001b[4~", 0x23, false)]
	[InlineData("\u001bOP", 0x70, false)]
	[InlineData("\u001b[15~", 0x74, false)]
	[InlineData("\r", 0x0D, false)]
	[InlineData("\u007f", 0x08, false)]
	[InlineData("\t", 0x09, false)]
	public void Sequences_BecomeKeys(string input, int virtualKey, bool shift)
	{
		Decode(input);
		var key = Assert.Single(_keys);
		Assert.Equal(virtualKey, key.VirtualKey);
		Assert.Equal(shift, key.Shift);
	}

	[Fact]
	public void LoneEscape_IsTheEscapeKey()
	{
		Decode("\u001b");
		Assert.Equal(0x1B, Assert.Single(_keys).VirtualKey);
	}

	[Fact]
	public void AltLetter_IsTheLetterWithAlt()
	{
		Decode("\u001bx");
		Assert.Equal(new VtKey('X', 'x', false, false, true), Assert.Single(_keys));
	}

	[Fact]
	public void QueryReplies_AreDropped()
	{
		Decode("\u001b[?62;4c\u001b[6;20;10t");
		Assert.Empty(_keys);
	}

	[Fact]
	public void SequenceCutBetweenReads_IsKeptUntilTheRestArrives()
	{
		Decode("\u001b[1;", 0);
		Assert.Empty(_keys);
		Decode("2C", 0.01);
		Assert.Equal(0x27, Assert.Single(_keys).VirtualKey);
	}

	[Fact]
	public void StaleIncompleteSequence_IsGivenUp()
	{
		Decode("\u001b[1;", 0);
		Decode(string.Empty, VtInputDecoder.StaleSeconds + 0.01);
		Assert.Equal(0x1B, _keys[0].VirtualKey);
		Assert.False(_decoder.HasPending);
	}

	[Fact]
	public void Utf8_IsOneCharacter()
	{
		Decode("é");
		Assert.Equal('é', Assert.Single(_keys).Character);
	}

	[Fact]
	public void SgrMouse_PressMoveRelease_TracksTheLeftButton()
	{
		Decode("\u001b[<0;10;5M\u001b[<32;12;5M\u001b[<0;12;5m\u001b[<35;13;6M");
		Assert.Equal(
			[
				new VtMouse(9, 4, true, false, false, 0),
				new VtMouse(11, 4, true, true, false, 0),
				new VtMouse(11, 4, false, false, false, 0),
				new VtMouse(12, 5, false, true, false, 0),
			],
			_mouse);
	}

	[Fact]
	public void SgrMouse_RightPressDragRelease_TracksTheRightButton()
	{
		Decode("\u001b[<2;10;5M\u001b[<34;14;5M\u001b[<2;14;5m");
		Assert.Equal(
			[
				new VtMouse(9, 4, false, false, false, 0, true),
				new VtMouse(13, 4, false, true, false, 0, true),
				new VtMouse(13, 4, false, false, false, 0, false),
			],
			_mouse);
	}

	[Fact]
	public void MenuKey_AndShiftF10_OpenTheMenu()
	{
		Decode("\u001b[29~\u001b[21;2~");
		Assert.Equal([(0x5D, false), (0x79, true)], _keys.Select(key => ((int) key.VirtualKey, key.Shift)));
	}

	[Fact]
	public void SgrMouse_Wheel_IsAStepAwayOrToward()
	{
		Decode("\u001b[<64;1;1M\u001b[<65;1;1M");
		Assert.Equal([1, -1], _mouse.Select(m => m.Wheel));
	}

	[Fact]
	public void SgrMouse_SecondPressSoonOnTheSameCell_IsADoubleClick()
	{
		Decode("\u001b[<0;3;3M\u001b[<0;3;3m", 0);
		Decode("\u001b[<0;3;3M", 0.2);
		Assert.True(_mouse[^1].DoubleClick);
		Assert.False(_mouse[0].DoubleClick);
	}

	private void Decode(string text, double now = 0)
		=> _decoder.Decode(Encoding.UTF8.GetBytes(text), _keys, _mouse, now);
}
