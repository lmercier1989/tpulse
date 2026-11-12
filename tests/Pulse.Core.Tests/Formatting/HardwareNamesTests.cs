using Pulse.Core.Formatting;

namespace Pulse.Core.Tests.Formatting;

public sealed class HardwareNamesTests
{
	[Theory]
	[InlineData("13th Gen Intel(R) Core(TM) i7-13700K", "13th Gen Intel Core i7-13700K")]
	[InlineData("Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz", "Intel Core i7-8700")]
	[InlineData("AMD Ryzen 7 5800X 8-Core Processor            ", "AMD Ryzen 7 5800X 8-Core")]
	[InlineData("", "CPU")]
	public void CleanCpuName_DropsMarketingNoise(string raw, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, HardwareNames.CleanCpuName(raw));
	}
}
