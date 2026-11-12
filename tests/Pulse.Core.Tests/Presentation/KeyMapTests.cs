using Pulse.Core.Presentation;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Presentation;

/// <summary>A click on the footer presses the key its hint names.</summary>
public sealed class KeyMapTests
{
	private static readonly string Footer = HeaderLine.FooterLine(140, KeyMap.Hints(9, replay: false), "1 min · every 1 s · sixel");

	[Theory]
	[InlineData("pause", "Space")]
	[InlineData("menu", "Right-click")]
	[InlineData("help", "?")]
	[InlineData("quit", "Q")]
	[InlineData("back ", "Backspace")]
	[InlineData("1 min", "H")]
	[InlineData("every", "R")]
	public void FooterKeyAt_AHint_IsItsKey(string text, string key)
	{
		// Act
		var found = KeyMap.FooterKeyAt(Footer, Footer.IndexOf(text, StringComparison.Ordinal));

		// Assert
		Assert.Equal(key, found?.Key);
	}

	[Theory]
	[InlineData("scroll")]
	[InlineData("graphs")]
	[InlineData("sixel")]
	public void FooterKeyAt_APartWithoutOneKey_DoesNothing(string text)
		=> Assert.Null(KeyMap.FooterKeyAt(Footer, Footer.IndexOf(text, StringComparison.Ordinal)));

	[Fact]
	public void FooterKeyAt_ASeparatorOrTheGap_DoesNothing()
	{
		// Act & Assert
		Assert.Null(KeyMap.FooterKeyAt(Footer, Footer.IndexOf('·', StringComparison.Ordinal)));
		Assert.Null(KeyMap.FooterKeyAt(Footer, Footer.IndexOf("Q quit", StringComparison.Ordinal) + 10));
		Assert.Null(KeyMap.FooterKeyAt(Footer, -1));
		Assert.Null(KeyMap.FooterKeyAt(Footer, Footer.Length));
	}

	[Fact]
	public void FooterKeyAt_ReturnsTheWholeHint_ToLightIt()
	{
		// Arrange
		var start = Footer.IndexOf("Space pause", StringComparison.Ordinal);

		// Act
		var found = KeyMap.FooterKeyAt(Footer, start + 7);

		// Assert
		Assert.Equal(("Space", start, "Space pause".Length), found);
	}

	[Fact]
	public void FooterKeyAt_TheHintsOfMessages_AreClickable()
	{
		// Arrange
		var selection = Messages.Selection(65);
		var back = Messages.Back(45);

		// Act & Assert
		Assert.Equal("E", KeyMap.FooterKeyAt(selection, selection.IndexOf("E save", StringComparison.Ordinal))?.Key);
		Assert.Equal("Esc", KeyMap.FooterKeyAt(selection, selection.IndexOf("clear", StringComparison.Ordinal))?.Key);
		Assert.Null(KeyMap.FooterKeyAt(selection, 2));
		Assert.Equal("End", KeyMap.FooterKeyAt(back, back.IndexOf("live", StringComparison.Ordinal))?.Key);
		Assert.Equal("N", KeyMap.FooterKeyAt("● rec · notify · 1 min", 9)?.Key);
	}
}
