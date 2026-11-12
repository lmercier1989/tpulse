using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class CounterPathTests
{
	[Theory]
	[InlineData(@"\Web Service(_Total)\Current Connections", null, "Web Service", "_Total", "Current Connections")]
	[InlineData(@"Memory\Available MBytes", null, "Memory", null, "Available MBytes")]
	[InlineData(@"\\sql01\Process(sqlservr)\% Processor Time", "sql01", "Process", "sqlservr", "% Processor Time")]
	[InlineData(@"""\LogicalDisk(C:)\% Free Space""", null, "LogicalDisk", "C:", "% Free Space")]
	[InlineData(@"\Network Interface(Intel[R] Ethernet)\Bytes Total/sec", null, "Network Interface", "Intel[R] Ethernet", "Bytes Total/sec")]
	public void TryParse_ValidPaths_SplitsTheirParts(string text, string? machine, string obj, string? instance, string counter)
	{
		// Act
		var parsed = CounterPath.TryParse(text, out var path);

		// Assert
		Assert.True(parsed);
		Assert.Equal(machine, path.Machine);
		Assert.Equal(obj, path.Object);
		Assert.Equal(instance, path.Instance);
		Assert.Equal(counter, path.Counter);
	}

	[Theory]
	[InlineData("")]
	[InlineData("Memory")]
	[InlineData(@"\Memory\")]
	[InlineData(@"\\\Memory\Available MBytes")]
	[InlineData(@"\(x)\Counter")]
	[InlineData(@"\Object(open\Counter")]
	public void TryParse_InvalidPaths_AreRejected(string text)
	{
		// Act & Assert
		Assert.False(CounterPath.TryParse(text, out _));
	}

	[Theory]
	[InlineData(@"\Processor(_Total)\% Processor Time", CounterUnit.Percent)]
	[InlineData(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", CounterUnit.BytesPerSecond)]
	[InlineData(@"\Memory\Available Bytes", CounterUnit.Bytes)]
	[InlineData(@"\Web Service(_Total)\Get Requests/sec", CounterUnit.PerSecond)]
	[InlineData(@"\Web Service(_Total)\Current Connections", CounterUnit.Number)]
	[InlineData(@"\Memory\Available MBytes", CounterUnit.Number)]
	public void Unit_FollowsTheCounterName(string text, CounterUnit expected)
	{
		// Arrange
		CounterPath.TryParse(text, out var path);

		// Act & Assert
		Assert.Equal(expected, path.Unit);
	}

	[Fact]
	public void ToPath_OnAnotherHost_PrefixesThePathUnlessItNamesAMachine()
	{
		// Arrange
		CounterPath.TryParse(@"Memory\Available MBytes", out var local);
		CounterPath.TryParse(@"\\web02\Memory\Available MBytes", out var named);

		// Act & Assert
		Assert.Equal(@"\Memory\Available MBytes", local.ToPath(null));
		Assert.Equal(@"\\sql01\Memory\Available MBytes", local.ToPath("sql01"));
		Assert.Equal(@"\\web02\Memory\Available MBytes", named.ToPath("sql01"));
	}

	[Fact]
	public void IsWildcard_OnlyForAStarInTheInstance()
	{
		// Arrange
		CounterPath.TryParse(@"\Process(*)\Handle Count", out var all);
		CounterPath.TryParse(@"\Process(w3wp)\Handle Count", out var one);

		// Act & Assert
		Assert.True(all.IsWildcard);
		Assert.False(one.IsWildcard);
		Assert.Equal("Process(*)", all.Source);
	}
}
