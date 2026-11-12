using System.Globalization;

using Pulse.Core.Formatting;
using Pulse.Core.Metrics;
using Pulse.Core.Presentation;

namespace Pulse.Core.Tests.Presentation;

/// <summary>One way to write a length of time, one key map for every surface, one layout for a moment in text.</summary>
public sealed class ConsistencyTests
{
	[Theory]
	[InlineData(0.5, "0.5 s")]
	[InlineData(2.25, "2.25 s")]
	[InlineData(30, "30 s")]
	[InlineData(59.6, "1 min")]
	[InlineData(60, "1 min")]
	[InlineData(65, "1 min 5 s")]
	[InlineData(300, "5 min")]
	[InlineData(725, "12 min")]
	[InlineData(3900, "1 h 5 min")]
	[InlineData(7200, "2 h")]
	[InlineData(183600, "2 d 3 h")]
	public void Duration_OneWayEverywhere(double seconds, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, Units.Duration(seconds, CultureInfo.InvariantCulture));
		Assert.Equal(expected, Core.Settings.PulseSettings.FormatDuration(seconds, CultureInfo.InvariantCulture));
	}

	[Fact]
	public void Share_HasOneDecimal()
	{
		// Act & Assert
		Assert.Equal("41.0%", Units.Share(41, CultureInfo.InvariantCulture));
		Assert.Equal("0.4%", Units.Share(0.42, CultureInfo.InvariantCulture));
		Assert.Equal(Units.Unavailable, Units.Share(double.NaN));
	}

	[Theory]
	[InlineData(Surface.Terminal)]
	[InlineData(Surface.Window)]
	public void KeyMap_NoKeyMeansTwoThings(Surface surface)
	{
		// Arrange
		var rows = KeyMap.For(surface, ["devenv"]).SelectMany(group => group.Rows).Where(row => row.Marker is null).ToList();

		// Act
		var twice = rows.GroupBy(row => row.Keys).Where(group => group.Count() > 1).Select(group => group.Key).ToList();

		// Assert
		Assert.Empty(twice);
		Assert.Contains(rows, row => row.Keys == "8" && row.Action.Contains("devenv", StringComparison.Ordinal));
		Assert.All(rows, row => Assert.True(row.Action.Length <= 46, row.Action)); // fits a column of the overview
	}

	[Fact]
	public void KeyMap_ALetterDoesTheSameOnBothSurfaces()
	{
		// Arrange
		var terminal = KeyMap.For(Surface.Terminal, []).SelectMany(group => group.Rows).Where(row => row.Keys.Length == 1).ToDictionary(row => row.Keys, row => row.Action);
		var window = KeyMap.For(Surface.Window, []).SelectMany(group => group.Rows).Where(row => row.Keys.Length == 1).ToDictionary(row => row.Keys, row => row.Action);

		// Act
		var different = terminal.Keys.Intersect(window.Keys).Where(key => terminal[key] != window[key]).ToList();

		// Assert
		Assert.Empty(different);
		Assert.True(terminal.ContainsKey("D") && !window.ContainsKey("D"));
		Assert.True(window.ContainsKey("T") && !terminal.ContainsKey("T"));
	}

	[Fact]
	public void KeyMap_ListsEveryMarkerKindWithItsColour()
	{
		// Act
		var markers = KeyMap.For(Surface.Terminal, []).Single(group => group.Title == "Markers").Rows.Select(row => row.Marker).ToList();

		// Assert
		Assert.Equal(Enum.GetValues<EventKind>().Order(), markers.OfType<EventKind>().Order());
		Assert.NotEmpty(KeyMap.ShellCommands);
		Assert.Contains(KeyMap.ShellCommands, row => row.Keys.StartsWith("tpulse --", StringComparison.Ordinal));
	}

	[Fact]
	public void KeyMap_Replay_LeavesOutWhatOnlySamplingDoes()
	{
		// Act
		var rows = KeyMap.For(Surface.Terminal, [], replay: true).SelectMany(group => group.Rows).ToList();

		// Assert
		Assert.DoesNotContain(rows, row => row.Keys is "R" or "N");
		Assert.Contains(rows, row => row is { Keys: "Space", Action: "Play or stop the recording" });
	}

	[Fact]
	public void PanelText_LaysOutLikeThePanel()
	{
		// Arrange
		PanelLine[] lines =
		[
			new(PanelLineKind.Section, "MOMENT", "14:32:05", 0),
			new(PanelLineKind.Field, "dotnet", "×7", 1, Right: "41.0%"),
			new(PanelLineKind.Blank, string.Empty, string.Empty, 9),
		];

		// Act
		var text = PanelText.Render(lines, 30);

		// Assert
		Assert.Equal("MOMENT  14:32:05", text[0]);
		Assert.StartsWith("dotnet", text[1], StringComparison.Ordinal);
		Assert.EndsWith("41.0%", text[1], StringComparison.Ordinal);
		Assert.Equal(30, text[1].Length);
		Assert.Equal(string.Empty, text[2]);
	}

	[Fact]
	public void Messages_KeyHintsReadKeyThenVerb()
	{
		// Act & Assert
		Assert.Equal("Markers on", Messages.Switch("Markers", on: true));
		Assert.Equal("Paused · Space resume", Messages.Paused);
		Assert.EndsWith("· End live", Messages.Back(45), StringComparison.Ordinal);
		Assert.EndsWith("Esc clear", Messages.Selection(65), StringComparison.Ordinal);
	}
}
