using Pulse.Core.Interop;
using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class TopProcessesTests
{
	[Fact]
	public void Top_TakesTheLargestAboveTheMinimum()
	{
		// Arrange
		var loads = new Dictionary<string, (double Value, int Count)>
		{
			["idle"] = (0.01, 1),
			["msedge"] = (4.2, 24),
			["devenv"] = (1, 1),
			["dotnet"] = (2.5, 33),
			["code"] = (0.5, 9),
		};

		// Act
		var top = TopProcesses.Top(loads, minimum: 0.05);

		// Assert
		Assert.Equal(["msedge", "dotnet", "devenv", "code"], top.Select(load => load.Name));
		Assert.Equal(24, top[0].Count);
	}

	[Theory]
	[InlineData("devenv.exe", "devenv")]
	[InlineData("System", "System")]
	[InlineData("", "System Idle")]
	public void Clean_DropsExe(string image, string expected)
	{
		// Act & Assert
		Assert.Equal(expected, TopProcesses.Clean(image));
	}

	[Fact]
	public async Task Sample_ThisMachine_ListsBusyNames()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Arrange
		var snapshot = new ProcessSnapshot();
		var top = new TopProcesses();
		top.Sample(snapshot.Take(), snapshot);
		var spin = Task.Run(() =>
		{
			var until = Environment.TickCount64 + 600;
			while (Environment.TickCount64 < until)
			{
				// Busy for a moment, so this process shows up.
			}
		}, TestContext.Current.CancellationToken);
		await spin;

		// Act
		var (cpu, io) = top.Sample(snapshot.Take(), snapshot);

		// Assert
		Assert.NotEmpty(cpu);
		Assert.InRange(cpu.Count, 1, TopProcesses.Size);
		Assert.All(cpu, load => Assert.False(load.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
		Assert.True(cpu.Zip(cpu.Skip(1)).All(pair => pair.First.Value >= pair.Second.Value));
		Assert.True(io.Count <= TopProcesses.Size);
	}

	[Fact]
	public void FirstLine_CutsTheWindowsEventToOneRow()
	{
		// Act & Assert
		Assert.Equal("Faulting application name: devenv.exe", WindowsEventSource.FirstLine("  Faulting application name: devenv.exe\r\nFaulting module: x.dll"));
		Assert.Equal(string.Empty, WindowsEventSource.FirstLine(null));
		Assert.Equal(160, WindowsEventSource.FirstLine(new string('x', 400)).Length);
	}
}
