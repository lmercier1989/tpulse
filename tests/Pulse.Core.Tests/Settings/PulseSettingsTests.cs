using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.Tests.Settings;

public sealed class PulseSettingsTests
{
	[Fact]
	public void Normalize_OutOfRangeValues_AreClamped()
	{
		// Arrange
		var settings = new PulseSettings { HistorySeconds = 99_999, IntervalSeconds = 0.01, TargetFps = 1000 };

		// Act
		settings.Normalize();

		// Assert
		Assert.Equal(PulseSettings.MaxHistorySeconds, settings.HistorySeconds);
		Assert.Equal(PulseSettings.MinIntervalSeconds, settings.IntervalSeconds);
		Assert.Equal(PulseSettings.MaxFps, settings.TargetFps);
	}

	[Fact]
	public void Normalize_ShortChannelList_IsCompletedKeepingKnownEntries()
	{
		// Arrange
		var settings = new PulseSettings { Channels = [new ChannelSettings { Visible = false, Size = ChannelSize.Tall }] };

		// Act
		settings.Normalize();

		// Assert
		Assert.Equal(MetricKinds.Builtin.Count, settings.Channels.Length);
		Assert.False(settings.Channel(MetricKind.Cpu).Visible);
		Assert.Equal(ChannelSize.Tall, settings.Channel(MetricKind.Cpu).Size);
		Assert.True(settings.Channel(MetricKind.Network).Visible);
	}

	[Theory]
	[InlineData(0.0, 500.0, false)]
	[InlineData(700.0, 0.0, true)]
	[InlineData(double.NaN, 500.0, false)]
	public void Normalize_WindowBounds_DropsOnlyInvalidOnes(double width, double height, bool kept)
	{
		// Arrange
		var settings = new PulseSettings { Window = new WindowBounds { Left = 10, Top = 10, Width = width, Height = height } };

		// Act
		settings.Normalize();

		// Assert
		Assert.Equal(kept, settings.Window is not null);
	}

	[Fact]
	public void Clone_ChangingTheCopy_LeavesTheOriginal()
	{
		// Arrange
		var original = new PulseSettings { Window = new WindowBounds { Width = 700, Height = 500 } };

		// Act
		var copy = original.Clone();
		copy.Channel(MetricKind.Gpu).Visible = false;
		copy.Window!.Width = 300;
		copy.HistorySeconds = 300;

		// Assert
		Assert.True(original.Channel(MetricKind.Gpu).Visible);
		Assert.Equal(700, original.Window!.Width);
		Assert.Equal(60, original.HistorySeconds);
	}
}
