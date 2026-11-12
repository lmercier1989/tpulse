using Pulse.Core.Presentation;

namespace Pulse.Core.Tests.Presentation;

/// <summary>Backspace goes back one step: the closest thing first, the baseline last.</summary>
public sealed class GoBackTests
{
	[Theory]
	[InlineData(true, true, true, true, true, true, BackStep.LetGo)]
	[InlineData(false, true, true, true, true, true, BackStep.ZoomOut)]
	[InlineData(false, false, true, true, true, true, BackStep.ClearSelection)]
	[InlineData(false, false, false, true, true, true, BackStep.ShowGraphs)]
	[InlineData(false, false, false, false, true, true, BackStep.Live)]
	[InlineData(false, false, false, false, false, true, BackStep.ClearBaseline)]
	[InlineData(false, false, false, false, false, false, BackStep.Nothing)]
	public void Next_TakesTheClosestStepFirst(bool frozen, bool zoomed, bool selected, bool alone, bool held, bool baseline, BackStep expected)
		=> Assert.Equal(expected, GoBack.Next(frozen, zoomed, selected, alone, held, baseline));

	[Fact]
	public void Next_PressedUntilNothingIsLeft_ReachesLiveInOneStepPerThing()
	{
		// Arrange: frozen, zoomed, selected, one graph alone, looking back and a baseline, all at once.
		bool[] state = [true, true, true, true, true, true];
		var steps = new List<BackStep>();

		// Act
		for (var press = 0; press < 10; press++)
		{
			var step = GoBack.Next(state[0], state[1], state[2], state[3], state[4], state[5]);
			steps.Add(step);
			if (step == BackStep.Nothing)
			{
				break;
			}

			state[(int) step - 1] = false;
		}

		// Assert
		Assert.Equal(
			[BackStep.LetGo, BackStep.ZoomOut, BackStep.ClearSelection, BackStep.ShowGraphs, BackStep.Live, BackStep.ClearBaseline, BackStep.Nothing],
			steps);
	}
}
