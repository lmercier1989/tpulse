using System.Diagnostics;

using Pulse.Core.Interop;

namespace Pulse.Core.Tests.Interop;

/// <summary>Checks the SYSTEM_PROCESS_INFORMATION offsets against the running system, using this test process.</summary>
public sealed class ProcessSnapshotTests
{
	[Fact]
	public void Take_FindsThisProcessWithItsRealValues()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Arrange
		using var self = Process.GetCurrentProcess();
		var snapshot = new ProcessSnapshot();

		// Act
		var records = snapshot.Take().ToArray();

		// Assert
		Assert.True(records.Length > 10);
		var record = Assert.Single(records, r => r.Id == Environment.ProcessId);
		Assert.Equal(self.StartTime.ToUniversalTime().ToFileTimeUtc(), record.CreateTime, (double) TimeSpan.TicksPerSecond);
		Assert.InRange(record.CpuTime, 1, self.TotalProcessorTime.Ticks + TimeSpan.TicksPerSecond);
		Assert.InRange(record.PrivateBytes, 1024 * 1024, self.WorkingSet64 * 2);
		Assert.Contains(records, r => r.Id == record.ParentId);
		Assert.False(record.IsClaude);
	}

	[Fact]
	public void ImagePath_OfThisProcess_IsItsExecutable()
	{
		// Act
		var path = ProcessSnapshot.ImagePath(Environment.ProcessId);

		// Assert
		Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
	}
}
