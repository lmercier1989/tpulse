using System.Globalization;

using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;

namespace Pulse.Core.Tests.Presentation;

public sealed class ChannelTextTests
{
	private static readonly SystemInfo Info = new()
	{
		CpuName = "Test CPU",
		LogicalProcessors = 8,
		HasCpu = true,
		GpuName = null,
		HasMemory = true,
		HasDisk = true,
		HasNetwork = true,
		NetworkAdapters = "Test NIC",
	};

	[Fact]
	public void Describe_BeforeTheFirstSample_ShowsTheStaticDetail()
	{
		// Act
		var readout = ChannelText.Describe(Channel.Create(MetricKind.Cpu), Info, sample: null, showStats: true);

		// Assert
		Assert.Equal("CPU", readout.Title);
		Assert.Equal("Test CPU · 8 threads", readout.Detail);
		Assert.Equal(string.Empty, readout.PrimaryValue);
	}

	[Fact]
	public void Describe_CpuSample_ShowsValueStatsAndThermalZone()
	{
		// Arrange
		var channel = Channel.Create(MetricKind.Cpu);
		var sample = new MetricSnapshot { Time = 1, CpuPercent = 42, CpuTemperatureCelsius = 51 };
		channel.Append(sample);
		channel.Refresh(1, 60, 2);

		// Act
		var readout = ChannelText.Describe(channel, Info, sample, showStats: true);

		// Assert
		Assert.Equal("42%", readout.PrimaryValue);
		Assert.Equal("min 42% · max 42%", readout.Stats);
		Assert.EndsWith(OperatingSystem.IsWindows() ? "ACPI zone 51 °C" : "temp 51 °C", readout.Detail);
		Assert.Equal(string.Empty, readout.Scale);
	}

	[Fact]
	public void Describe_Disk_HasAScaleAndTwoValues()
	{
		// Arrange
		var culture = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
		var channel = Channel.Create(MetricKind.Disk);
		var sample = new MetricSnapshot { Time = 1, DiskReadBytesPerSecond = 2_000_000, DiskWriteBytesPerSecond = 500 };
		channel.Append(sample);
		channel.Refresh(1, 60, 2);

		// Act
		ChannelReadout readout;
		try
		{
			readout = ChannelText.Describe(channel, Info, sample, showStats: false);
		}
		finally
		{
			CultureInfo.CurrentCulture = culture;
		}

		// Assert
		Assert.True(readout.HasSecondary);
		Assert.Equal("2.00 MB/s", readout.PrimaryValue);
		Assert.Equal("500 B/s", readout.SecondaryValue);
		Assert.Equal("5.00 MB/s", readout.Scale);
		Assert.Equal(string.Empty, readout.Stats);
	}

	[Fact]
	public void Describe_NoGpu_SaysWhy()
	{
		// Act
		var readout = ChannelText.Describe(Channel.Create(MetricKind.Gpu), Info, new MetricSnapshot { Time = 1 }, showStats: true);

		// Assert
		Assert.Equal("No hardware GPU found", readout.Unavailable);
		Assert.False(readout.HasSecondary);
	}
}
