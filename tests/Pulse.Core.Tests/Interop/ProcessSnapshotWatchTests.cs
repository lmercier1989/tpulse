using System.Diagnostics;

using Pulse.Core.Interop;

namespace Pulse.Core.Tests.Interop;

/// <summary>Checks the thread and handle offsets and the name marking against this test process.</summary>
public sealed class ProcessSnapshotWatchTests
{
	[Fact]
	public void Take_MarksWatchedNames_AndReadsThreadsAndHandles()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Arrange
		using var self = Process.GetCurrentProcess();
		var snapshot = new ProcessSnapshot { WatchedNames = ["", "no-such-image.exe", self.ProcessName + ".exe"] };

		// Act
		var record = Assert.Single(snapshot.Take().ToArray(), r => r.Id == Environment.ProcessId);

		// Assert
		Assert.Equal(2, record.Watch);
		Assert.InRange(record.Threads, Math.Max(1, self.Threads.Count / 2), self.Threads.Count * 2);
		Assert.InRange(record.Handles, Math.Max(1, self.HandleCount / 2), self.HandleCount * 2);
	}

	[Fact]
	public void Take_EmptyWatchedName_NeverMatchesTheIdleProcess()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Arrange
		var snapshot = new ProcessSnapshot { WatchedNames = [""] };

		// Act & Assert
		Assert.All(snapshot.Take().ToArray(), record => Assert.Equal(-1, record.Watch));
	}
}
