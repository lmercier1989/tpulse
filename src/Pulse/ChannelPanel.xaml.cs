using System.Windows;
using System.Windows.Controls;

using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Settings;
using Pulse.Rendering;

namespace Pulse;

/// <summary>
/// One graph of the window: a header line (name, details, window min/max, current values) above the trace
/// area. Text is only touched when it changes, so a steady reading causes no layout work.
/// </summary>
internal sealed partial class ChannelPanel : UserControl
{
	private readonly Channel _channel;
	private readonly SystemInfo _info;

	public ChannelPanel(MetricKind kind, Channel channel, ScopeStyle style, SystemInfo info)
	{
		InitializeComponent();
		Kind = kind;
		_channel = channel;
		_info = info;

		Dot.Fill = style.PrimaryBrush;
		PrimaryKey.Fill = style.PrimaryBrush;
		SecondaryKey.Fill = style.SecondaryBrush;
		Scope.Attach(channel, style);

		var readout = ChannelText.Describe(channel, info, sample: null, showStats: false);
		NameText.Text = readout.Title.ToUpperInvariant();
		PrimaryLabel.Text = readout.PrimaryLabel;
		SecondaryLabel.Text = readout.SecondaryLabel;
		PrimaryKey.Visibility = Show(readout.HasSecondary);
		PrimaryLabel.Visibility = Show(readout.PrimaryLabel.Length > 0);
		SecondaryKey.Visibility = SecondaryLabel.Visibility = SecondaryValue.Visibility = Show(readout.HasSecondary);
		Apply(readout);

		if (readout.Unavailable is not null)
		{
			MessageText.Text = readout.Unavailable;
			MessageText.Visibility = Visibility.Visible;
		}

		Header.MouseLeftButtonUp += (_, _) => HeaderClicked?.Invoke(this, EventArgs.Empty);
		PlotHost.MouseLeftButtonDown += (_, e) =>
		{
			if (e.ClickCount == 2)
			{
				PlotDoubleClicked?.Invoke(this, EventArgs.Empty);
				e.Handled = true;
			}
		};
	}

	public event EventHandler? HeaderClicked;

	public event EventHandler? PlotDoubleClicked;

	public MetricKind Kind { get; }

	/// <summary>Whether the trace area is shown (not collapsed to the header).</summary>
	public bool IsPlotVisible { get; private set; } = true;

	/// <summary>Header plus plot height of the current layout, used as the row weight.</summary>
	public double DesignedHeight { get; private set; }

	public void ApplyLayout(ChannelSize size, bool compact)
	{
		var headerHeight = compact ? 20.0 : 26.0;
		var plotHeight = size switch
		{
			ChannelSize.Tall => compact ? 120.0 : 230.0,
			ChannelSize.Collapsed => 0.0,
			_ => compact ? 52.0 : 96.0,
		};

		Header.Height = headerHeight;
		IsPlotVisible = size != ChannelSize.Collapsed;
		PlotHost.Visibility = Show(IsPlotVisible);
		if (plotHeight > 0)
		{
			Scope.DesiredPlotHeight = plotHeight;
		}

		DetailText.Visibility = Show(!compact);
		StatsText.Visibility = Show(!compact);
		DesignedHeight = headerHeight + plotHeight;
	}

	public void Present(MetricSnapshot sample, bool showStats) => Apply(ChannelText.Describe(_channel, _info, sample, showStats));

	private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

	private static void Set(TextBlock block, string text)
	{
		if (!string.Equals(block.Text, text, StringComparison.Ordinal))
		{
			block.Text = text;
		}
	}

	private void Apply(ChannelReadout readout)
	{
		Set(DetailText, readout.Detail);
		Set(StatsText, readout.Stats);
		Set(PrimaryValue, readout.PrimaryValue);
		Set(SecondaryValue, readout.SecondaryValue);
		Set(ScaleText, readout.Scale);
	}
}
