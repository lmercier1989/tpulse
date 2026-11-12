namespace Pulse.Core.Presentation;

/// <summary>What one press of Backspace undoes.</summary>
public enum BackStep
{
	/// <summary>Live, nothing looked into: nothing to go back from.</summary>
	Nothing,

	/// <summary>Let go of the moment frozen with the mouse.</summary>
	LetGo,

	/// <summary>Zoom out to the history before the last zoom.</summary>
	ZoomOut,

	/// <summary>Clear the selected range.</summary>
	ClearSelection,

	/// <summary>Show the graphs from before "Only" again.</summary>
	ShowGraphs,

	/// <summary>Back to live: from looking back, from a pause, or stop a recording that plays.</summary>
	Live,

	/// <summary>Clear the baseline.</summary>
	ClearBaseline,
}

/// <summary>
/// Backspace goes back one step, whatever the screen shows: the closest thing first (a frozen moment), then the view (a
/// zoom, a selection, one graph alone), then the time (back to live), and the baseline last, because it is kept on
/// purpose. Pressed again and again it returns to the live picture.
/// </summary>
public static class GoBack
{
	/// <param name="frozen">A moment is frozen with the mouse.</param>
	/// <param name="zoomed">The view is zoomed in (Enter, or "Zoom in around here").</param>
	/// <param name="selected">A range of time is selected.</param>
	/// <param name="alone">One graph is shown alone ("Only CPU").</param>
	/// <param name="held">The view is held: looking back, paused, or a recording that plays.</param>
	/// <param name="baseline">A baseline is set.</param>
	public static BackStep Next(bool frozen, bool zoomed, bool selected, bool alone, bool held, bool baseline)
		=> frozen ? BackStep.LetGo
			: zoomed ? BackStep.ZoomOut
			: selected ? BackStep.ClearSelection
			: alone ? BackStep.ShowGraphs
			: held ? BackStep.Live
			: baseline ? BackStep.ClearBaseline
			: BackStep.Nothing;
}
