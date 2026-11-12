using System.Globalization;

using Pulse.Core.Formatting;

namespace Pulse.Core.Tests.Formatting;

public sealed class UnitsTests
{
	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	[Theory]
	[InlineData(42.4, "42%")]
	[InlineData(99.5, "100%")]
	[InlineData(0.0, "0%")]
	[InlineData(-3.0, "0%")]
	public void Percent_RoundsToWholePercent(double value, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, Units.Percent(value, Invariant));
	}

	[Theory]
	[InlineData(0.0, "0 B/s")]
	[InlineData(512.0, "512 B/s")]
	[InlineData(1234.0, "1.23 KB/s")]
	[InlineData(12_400_000.0, "12.4 MB/s")]
	[InlineData(124_000_000.0, "124 MB/s")]
	[InlineData(999_600.0, "1.00 MB/s")]
	[InlineData(2_500_000_000.0, "2.50 GB/s")]
	public void BytesPerSecond_UsesDecimalPrefixesAndThreeDigits(double value, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, Units.BytesPerSecond(value, Invariant));
	}

	[Theory]
	[InlineData(26_000.0, "26.0 Kbps")]
	[InlineData(1_000_000_000.0, "1.00 Gbps")]
	public void BitsPerSecond_UsesDecimalPrefixes(double value, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, Units.BitsPerSecond(value, Invariant));
	}

	[Fact]
	public void Bytes_UsesBinaryPrefixes()
	{
		// Act & Assert
		Assert.Equal("32.0 GB", Units.Bytes(32.0 * 1024 * 1024 * 1024, Invariant));
	}

	[Fact]
	public void BytesOf_ShowsBothSizesInTheUnitOfTheTotal()
	{
		// Arrange
		const double gibibyte = 1024.0 * 1024 * 1024;

		// Act
		var text = Units.BytesOf(1.1 * gibibyte, 6 * gibibyte, Invariant);

		// Assert
		Assert.Equal("1.1 / 6.0 GB", text);
	}

	[Fact]
	public void Formatting_FollowsTheCultureDecimalSeparator()
	{
		// Act
		var text = Units.BytesPerSecond(12_400_000, CultureInfo.GetCultureInfo("nl-NL"));

		// Assert
		Assert.Equal("12,4 MB/s", text);
	}

	[Fact]
	public void TemperatureAndPower_AreWholeNumbers()
	{
		// Act & Assert
		Assert.Equal("47 °C", Units.Celsius(47.4, Invariant));
		Assert.Equal("112 W", Units.Watts(111.8, Invariant));
	}

	[Theory]
	[InlineData(0.0, "0/min")]
	[InlineData(512.4, "512/min")]
	[InlineData(12_400.0, "12.4k/min")]
	[InlineData(1_240_000.0, "1.24M/min")]
	public void TokensPerMinute_UsesKAndM(double value, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, Units.TokensPerMinute(value, Invariant));
	}

	[Fact]
	public void UnavailableReading_ShowsNotAvailable()
	{
		// Act & Assert
		Assert.Equal(Units.Unavailable, Units.Percent(double.NaN, Invariant));
		Assert.Equal(Units.Unavailable, Units.BytesPerSecond(double.NaN, Invariant));
		Assert.Equal(Units.Unavailable, Units.Celsius(double.NaN, Invariant));
		Assert.Equal(Units.Unavailable, Units.BytesOf(1, double.NaN, Invariant));
	}
}
