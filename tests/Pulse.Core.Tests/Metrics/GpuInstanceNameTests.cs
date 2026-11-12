using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class GpuInstanceNameTests
{
	[Fact]
	public void TryParseEngine_TaskManagerInstance_ReturnsAdapterPhysicalAndEngine()
	{
		// Act
		var parsed = GpuInstanceName.TryParseEngine(
			"pid_4568_luid_0x00000000_0x0000F1B4_phys_0_eng_3_engtype_VideoDecode",
			out var luid,
			out var physical,
			out var engine);

		// Assert
		Assert.True(parsed);
		Assert.Equal(0x0000F1B4UL, luid);
		Assert.Equal(0, physical);
		Assert.Equal(3, engine);
	}

	[Fact]
	public void TryParseEngine_HighLuidPartAndLinkedAdapter_ParsesAllParts()
	{
		// Act
		var parsed = GpuInstanceName.TryParseEngine(
			"pid_12_luid_0x00000001_0x0000ABCD_phys_1_eng_12_engtype_3D",
			out var luid,
			out var physical,
			out var engine);

		// Assert
		Assert.True(parsed);
		Assert.Equal((1UL << 32) | 0xABCD, luid);
		Assert.Equal(1, physical);
		Assert.Equal(12, engine);
	}

	[Fact]
	public void TryParseAdapter_AdapterMemoryInstance_ReturnsAdapterAndPhysical()
	{
		// Act
		var parsed = GpuInstanceName.TryParseAdapter("luid_0x00000000_0x0000F1B4_phys_0", out var luid, out var physical);

		// Assert
		Assert.True(parsed);
		Assert.Equal(0x0000F1B4UL, luid);
		Assert.Equal(0, physical);
	}

	[Theory]
	[InlineData("")]
	[InlineData("_Total")]
	[InlineData("pid_1_luid_0xZZZZ_0x00000001_phys_0_eng_0_engtype_3D")]
	[InlineData("pid_1_luid_0x00000000_0x0000F1B4_eng_0_engtype_3D")]
	[InlineData("pid_1_luid_0x00000000_0x0000F1B4_phys_0_engtype_3D")]
	public void TryParseEngine_MalformedName_ReturnsFalse(string name)
	{
		// Act
		var parsed = GpuInstanceName.TryParseEngine(name, out _, out _, out _);

		// Assert
		Assert.False(parsed);
	}

	[Fact]
	public void ComposeLuid_MatchesTheLuidParsedFromCounterNames()
	{
		// Arrange
		GpuInstanceName.TryParseAdapter("luid_0xFFFFFFFF_0x80000001_phys_0", out var parsed, out _);

		// Act
		var composed = GpuInstanceName.ComposeLuid(high: -1, low: 0x80000001);

		// Assert
		Assert.Equal(parsed, composed);
	}
}
