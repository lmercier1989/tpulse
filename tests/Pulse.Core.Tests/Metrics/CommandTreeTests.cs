using System.Diagnostics;

using Pulse.Core.Interop;
using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>The processes a measured command started: on snapshots built here, and on a real command that leaves one running.</summary>
public sealed class CommandTreeTests
{
	private const int Root = 100;

	[Fact]
	public void Follow_ChildrenAndTheirChildren_BelongToTheCommand()
	{
		// Arrange
		ProcessRecord[] records = [Record(Root, 1, 10), Record(101, Root, 11), Record(102, 101, 12), Record(200, 1, 5)];
		var members = new Dictionary<int, long>();
		var rootCreated = -1L;

		// Act
		CommandTree.Follow(records, Root, ref rootCreated, members);

		// Assert
		Assert.Equal([Root, 101, 102], members.Keys.Order());
		Assert.Equal(10, rootCreated);
	}

	[Fact]
	public void Follow_ParentEndedSince_ItsChildStillBelongs()
	{
		// Arrange
		var members = new Dictionary<int, long>();
		var rootCreated = -1L;
		CommandTree.Follow([Record(Root, 1, 10), Record(101, Root, 11)], Root, ref rootCreated, members);

		// Act: the build ended; the server it started runs on.
		CommandTree.Follow([Record(102, 101, 20)], Root, ref rootCreated, members);

		// Assert
		Assert.Contains(102, members.Keys);
	}

	[Fact]
	public void Follow_IdHandedToAnotherProcess_IsForgotten()
	{
		// Arrange
		var members = new Dictionary<int, long>();
		var rootCreated = -1L;
		CommandTree.Follow([Record(Root, 1, 10), Record(101, Root, 11)], Root, ref rootCreated, members);

		// Act: 101 ended, and its id went to a process of someone else, which starts a child of its own.
		CommandTree.Follow([Record(101, 1, 50), Record(300, 101, 60)], Root, ref rootCreated, members);

		// Assert
		Assert.DoesNotContain(101, members.Keys);
		Assert.DoesNotContain(300, members.Keys);
	}

	[Fact]
	public void Follow_ProcessOlderThanItsParent_IsNotAChild()
	{
		// Arrange
		var members = new Dictionary<int, long>();
		var rootCreated = -1L;

		// Act
		CommandTree.Follow([Record(Root, 1, 10), Record(103, Root, 5)], Root, ref rootCreated, members);

		// Assert
		Assert.DoesNotContain(103, members.Keys);
	}

	[Fact]
	public void Running_CommandThatStartsABackgroundProcess_NamesIt()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "No process snapshot on this platform.");

		// Arrange: a shell that waits a moment, starts a process in the background and ends.
		var start = OperatingSystem.IsWindows()
			? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { Arguments = "/d /c \"ping -n 2 127.0.0.1 >nul & start /b ping -n 10 127.0.0.1 >nul\"" }
			: new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 1; sleep 10 &" } };
		start.UseShellExecute = false;
		start.CreateNoWindow = true;
		var tree = new CommandTree();
		List<LeftProcess> left = [];
		try
		{
			// Act
			using (var process = Process.Start(start)!)
			{
				tree.Start(process.Id);
				process.WaitForExit();
			}

			left = tree.Running();

			// Assert
			var expected = OperatingSystem.IsWindows() ? "ping" : "sleep";
			Assert.Contains(left, process => process.Name.Equals(expected, StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			foreach (var process in left)
			{
				try
				{
					using var running = Process.GetProcessById(process.Id);
					running.Kill();
				}
				catch (ArgumentException)
				{
					// Ended already.
				}
			}
		}
	}

	private static ProcessRecord Record(int id, int parent, long created) => new() { Id = id, ParentId = parent, CreateTime = created };
}
