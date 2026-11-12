namespace Pulse.Core.Settings;

/// <summary>
/// The order you put graphs and panel sections in: a list of names, remembered in the settings. A name the list does
/// not hold keeps its natural place after the ones it does, so a graph or section that is new keeps showing up.
/// </summary>
public static class LayoutOrder
{
	/// <summary><paramref name="items"/> in the order of <paramref name="order"/>, the rest in their own order after them.</summary>
	public static List<T> Sort<T>(IReadOnlyList<T> items, Func<T, string> nameOf, IReadOnlyList<string> order)
		=> [.. items
			.Select((item, index) => (Item: item, Rank: IndexOf(order, nameOf(item)) is var at and >= 0 ? at : order.Count + index))
			.OrderBy(pair => pair.Rank)
			.Select(pair => pair.Item)];

	/// <summary>
	/// The new order after moving <paramref name="name"/> one place up (<paramref name="delta"/> -1) or down (+1) in
	/// <paramref name="all"/>, the names as they are ordered now: it trades places with the nearest neighbour that
	/// <paramref name="isShown"/>, so a hidden one in between keeps its own place.
	/// </summary>
	public static List<string> Move(IReadOnlyList<string> all, Func<string, bool> isShown, string name, int delta)
	{
		var list = all.ToList();
		var at = list.IndexOf(name);
		if (at < 0 || delta == 0)
		{
			return list;
		}

		var step = Math.Sign(delta);
		for (var to = at + step; to >= 0 && to < list.Count; to += step)
		{
			if (isShown(list[to]))
			{
				(list[at], list[to]) = (list[to], list[at]);
				break;
			}
		}

		return list;
	}

	private static int IndexOf(IReadOnlyList<string> order, string name)
	{
		for (var i = 0; i < order.Count; i++)
		{
			if (string.Equals(order[i], name, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}

		return -1;
	}
}
