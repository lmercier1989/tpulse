using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

public sealed class TerminalRepliesTests
{
	[Fact]
	public void Parse_WindowsTerminalReply_FindsSixelAndSizes()
	{
		// Arrange
		const string reply = "\u001b[6;20;10t\u001b[4;920;1500t\u001b[?61;4;6;7;14;21;22;23;24;28;32;42c";

		// Act
		var replies = TerminalReplies.Parse(reply);

		// Assert
		Assert.True(replies.SupportsSixel);
		Assert.Equal(10, replies.CellWidth);
		Assert.Equal(20, replies.CellHeight);
		Assert.Equal(1500, replies.AreaWidth);
		Assert.Equal(920, replies.AreaHeight);
	}

	[Theory]
	[InlineData("\u001b[?61;6;7;22c")]
	[InlineData("\u001b[?4;6c")]
	public void Parse_NoSixelAttribute_ReportsNoSixel(string reply)
	{
		// Act
		var replies = TerminalReplies.Parse(reply);

		// Assert
		Assert.False(replies.SupportsSixel);
	}

	[Fact]
	public void Parse_NoReply_KnowsNothing()
	{
		// Act
		var replies = TerminalReplies.Parse(string.Empty);

		// Assert
		Assert.Null(replies.SupportsSixel);
		Assert.Null(replies.ResolveCellSize(80, 24));
	}

	[Fact]
	public void IsComplete_WaitsForTheDeviceAttributes()
	{
		// Act & Assert
		Assert.False(TerminalReplies.IsComplete("\u001b[6;20;10t"));
		Assert.True(TerminalReplies.IsComplete("\u001b[6;20;10t\u001b[?61;4c"));
	}

	[Fact]
	public void ResolveCellSize_WithoutCellReply_DividesTheTextArea()
	{
		// Arrange
		var replies = TerminalReplies.Parse("\u001b[4;920;1500t\u001b[?61;4c");

		// Act
		var cell = replies.ResolveCellSize(150, 46);

		// Assert
		Assert.Equal((10, 20), cell);
	}
}
