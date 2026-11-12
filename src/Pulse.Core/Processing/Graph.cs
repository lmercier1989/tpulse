using Pulse.Core.Metrics;
using Pulse.Core.Settings;

namespace Pulse.Core.Processing;

/// <summary>
/// One graph of a launch: its data, and whether and how tall it is shown. A built-in graph shows as remembered, and a
/// change made with the keys is remembered for next time; the extra graphs of <c>-process</c> and
/// <c>-counter</c> live for this launch only.
/// </summary>
public sealed class Graph
{
	/// <summary>Graphs get the keys 1 to 9 and then 0.</summary>
	public const int MaximumKeys = 10;

	private readonly ChannelSettings _settings;
	private readonly ChannelSettings? _remembered;

	private Graph(Channel channel, ChannelSettings settings, ChannelSettings? remembered)
	{
		Channel = channel;
		_settings = settings;
		_remembered = remembered;
	}

	public Channel Channel { get; }

	public MetricKind Kind => Channel.Kind;

	public bool Visible => _settings.Visible;

	public ChannelSize Size => _settings.Size;

	/// <summary>
	/// The graphs of a launch, in key order: the built-in ones (keys 1 to 7), then one per watch (keys 8, 9 and 0),
	/// shown at normal size.
	/// </summary>
	/// <param name="session">The settings of this launch.</param>
	/// <param name="saved">The remembered settings.</param>
	/// <param name="info">What is followed: the watches.</param>
	/// <param name="capacity">Samples per trace; 0 for the default (a recording needs room for all its rows).</param>
	public static IReadOnlyList<Graph> CreateAll(PulseSettings session, PulseSettings saved, SystemInfo info, int capacity = 0)
	{
		var graphs = new List<Graph>(MetricKinds.Builtin.Count + 1 + info.Watches.Count);
		foreach (var kind in MetricKinds.Builtin)
		{
			graphs.Add(new Graph(Channel.Create(kind, capacity), session.Channel(kind), saved.Channel(kind)));
		}

		for (var i = 0; i < info.Watches.Count; i++)
		{
			graphs.Add(new Graph(Channel.ForWatch(info.Watches[i], i, capacity), new ChannelSettings(), null));
		}

		return graphs;
	}

	public void SetVisible(bool visible)
	{
		_settings.Visible = visible;
		if (_remembered is not null)
		{
			_remembered.Visible = visible;
		}
	}

	/// <summary>Shows the graph at <paramref name="size"/>.</summary>
	public void SetSize(ChannelSize size)
	{
		SetVisible(true);
		_settings.Size = size;
		if (_remembered is not null)
		{
			_remembered.Size = size;
		}
	}

	/// <summary>The next size of Shift + its key: normal, tall, collapsed; a hidden graph comes back at normal size.</summary>
	public ChannelSize NextSize() => !Visible
		? ChannelSize.Normal
		: Size switch
		{
			ChannelSize.Normal => ChannelSize.Tall,
			ChannelSize.Tall => ChannelSize.Collapsed,
			_ => ChannelSize.Normal,
		};

	/// <summary>The key of the graph at <paramref name="index"/>: '1' to '9', then '0'.</summary>
	public static char KeyOf(int index) => index < 9 ? (char) ('1' + index) : '0';

	/// <summary>
	/// "1 to 6", "7", "7 to 9, 0": the keys of <paramref name="count"/> graphs from key number <paramref name="first"/>
	/// (1-based; the tenth graph has key 0).
	/// </summary>
	public static string KeyRange(int first, int count)
	{
		var last = first + count - 1;
		if (count <= 1)
		{
			return KeyOf(first - 1).ToString();
		}

		return last < 10 ? $"{first} to {last}" : first == 9 ? "9, 0" : $"{first} to 9, 0";
	}
}
