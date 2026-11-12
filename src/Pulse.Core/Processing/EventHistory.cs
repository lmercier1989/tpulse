using Pulse.Core.Metrics;

namespace Pulse.Core.Processing;

/// <summary>
/// The events of a launch, oldest first, for the markers on the time axis and the event list of the info panel.
/// Only the newest <see cref="Capacity"/> are kept. Used from the UI thread only.
/// </summary>
public sealed class EventHistory
{
	public const int Capacity = 500;

	private readonly List<PulseEvent> _events = [];

	/// <summary>Changes whenever an event is added, so a renderer knows its markers are stale.</summary>
	public int Version { get; private set; }

	public int Count => _events.Count;

	public void Add(IEnumerable<PulseEvent> events)
	{
		foreach (var item in events)
		{
			// Events arrive in time order per source; keep the whole list ordered by time.
			var index = _events.Count;
			while (index > 0 && _events[index - 1].Time > item.Time)
			{
				index--;
			}

			_events.Insert(index, item);
			Version++;
		}

		if (_events.Count > Capacity)
		{
			_events.RemoveRange(0, _events.Count - Capacity);
		}
	}

	/// <summary>The events with a time in [<paramref name="from"/>, <paramref name="to"/>], oldest first.</summary>
	public IEnumerable<PulseEvent> Between(double from, double to)
	{
		foreach (var item in _events)
		{
			if (item.Time > to)
			{
				yield break;
			}

			if (item.Time >= from)
			{
				yield return item;
			}
		}
	}

	/// <summary>The newest <paramref name="count"/> events up to <paramref name="time"/>, newest first.</summary>
	public IReadOnlyList<PulseEvent> Latest(int count, double time = double.PositiveInfinity)
	{
		var result = new List<PulseEvent>(count);
		for (var i = _events.Count - 1; i >= 0 && result.Count < count; i--)
		{
			if (_events[i].Time <= time)
			{
				result.Add(_events[i]);
			}
		}

		return result;
	}
}
