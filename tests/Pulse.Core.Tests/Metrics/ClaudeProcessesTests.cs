using Pulse.Core.Interop;
using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class ClaudeProcessesTests
{
	[Fact]
	public void Classify_SplitsTheAppItsSessionsAndTheirTools()
	{
		// Arrange: explorer > app > (helper, engine > cmd > dotnet), and an unrelated process.
		ProcessRecord[] records =
		[
			Record(1, 0, 1, "explorer"),
			Record(10, 1, 2, "claude-app", claude: true),
			Record(11, 10, 3, "claude-helper", claude: true),
			Record(20, 10, 4, "claude-engine", claude: true),
			Record(30, 20, 5, "cmd"),
			Record(31, 30, 6, "dotnet"),
			Record(40, 1, 7, "notepad"),
		];
		var roles = new ClaudeRole[records.Length];

		// Act
		ClaudeProcesses.Classify(records, roles, [], record => record.Id == 20);

		// Assert
		Assert.Equal(
			new[] { ClaudeRole.None, ClaudeRole.App, ClaudeRole.App, ClaudeRole.Session, ClaudeRole.Tool, ClaudeRole.Tool, ClaudeRole.None },
			roles);
	}

	[Fact]
	public void Classify_ChildOfTheAppThatIsNotClaude_CountsForTheApp()
	{
		// Arrange
		ProcessRecord[] records = [Record(10, 1, 1, "claude-app", claude: true), Record(12, 10, 2, "conhost")];
		var roles = new ClaudeRole[records.Length];

		// Act
		ClaudeProcesses.Classify(records, roles, [], _ => false);

		// Assert
		Assert.Equal(ClaudeRole.App, roles[1]);
	}

	[Fact]
	public void Classify_ReusedParentId_IsNotAParent()
	{
		// Arrange: process 30 was started before the engine that now has its parent's old id.
		ProcessRecord[] records = [Record(20, 1, 50, "claude-engine", claude: true), Record(30, 20, 10, "old")];
		var roles = new ClaudeRole[records.Length];

		// Act
		ClaudeProcesses.Classify(records, roles, [], _ => true);

		// Assert
		Assert.Equal(ClaudeRole.None, roles[1]);
	}

	[Fact]
	public void Sample_OnThisMachine_ReturnsPlausibleNumbers()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Arrange
		var snapshot = new ProcessSnapshot();
		var processes = new ClaudeProcesses();
		processes.Sample(snapshot.Take());
		Thread.Sleep(200);

		// Act
		var usage = processes.Sample(snapshot.Take());

		// Assert
		Assert.InRange(usage.TotalCpu, 0, 100.0001);
		Assert.True(usage.TotalBytes >= 0);
		Assert.True(usage.Sessions >= 0 && usage.Tools >= 0);
	}

	private static ProcessRecord Record(int id, int parent, long created, string name, bool claude = false)
		=> new() { Id = id, ParentId = parent, CreateTime = created, CpuTime = name.Length, PrivateBytes = 1000, IsClaude = claude };
}
