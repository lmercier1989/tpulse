using Pulse.Core.CommandLine;

namespace Pulse.Core.Tests.CommandLine;

public sealed class DurationParserTests
{
	[Theory]
	[InlineData("30", 30.0)]
	[InlineData("30s", 30.0)]
	[InlineData("1.5s", 1.5)]
	[InlineData("500ms", 0.5)]
	[InlineData("1m", 60.0)]
	[InlineData("5min", 300.0)]
	[InlineData("2 sec", 2.0)]
	[InlineData(" 5M ", 300.0)]
	public void TryParseSeconds_ValidDuration_ReturnsSeconds(string text, double expected)
	{
		// Act
		var parsed = DurationParser.TryParseSeconds(text, out var seconds);

		// Assert
		Assert.True(parsed);
		Assert.Equal(expected, seconds, 1e-12);
	}

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("-5")]
	[InlineData("0")]
	[InlineData("5x")]
	[InlineData("1,5s")]
	public void TryParseSeconds_InvalidDuration_ReturnsFalse(string text)
	{
		// Act & Assert
		Assert.False(DurationParser.TryParseSeconds(text, out _));
	}
}
