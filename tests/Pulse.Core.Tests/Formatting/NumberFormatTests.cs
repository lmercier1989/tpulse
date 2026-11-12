using System.Globalization;

using Pulse.Core.Formatting;

namespace Pulse.Core.Tests.Formatting;

public sealed class NumberFormatTests
{
	[Theory]
	[InlineData(0, "0")]
	[InlineData(0.0042, "0.0042")]
	[InlineData(12.345, "12.3")]
	[InlineData(512, "512")]
	[InlineData(12_400, "12.4k")]
	[InlineData(1_240_000, "1.24M")]
	[InlineData(3_100_000_000, "3.10G")]
	[InlineData(-12.345, "-12.3")]
	[InlineData(double.NaN, "n/a")]
	public void Number_ThreeSignificantDigits(double value, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, Units.Number(value, CultureInfo.InvariantCulture));
	}
}
