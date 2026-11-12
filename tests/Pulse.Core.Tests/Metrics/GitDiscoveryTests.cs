using System.Diagnostics;

using Pulse.Core.Interop;
using Pulse.Core.Metrics;
using Pulse.Core.Presentation;

namespace Pulse.Core.Tests.Metrics;

/// <summary>Finding repositories without being told: by scanning folders, and through the folder a process works in.</summary>
public sealed class GitDiscoveryTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "PulseTests", "discover-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(_folder, recursive: true);
		}
	}

	[Fact]
	public void Scan_FindsRepositoriesUpToTheDepth_AndSkipsWhatItShould()
	{
		// Arrange: work\shop (depth 2), work\deep\er\app (depth 4), node_modules\lib, .hidden\x
		Repository("work", "shop");
		Repository("work", "deep", "er", "app");
		Repository("node_modules", "lib");
		Repository(".hidden", "x");
		Repository("work", "shop", "inner"); // Inside a repository: not looked for.

		// Act
		var found = GitDiscovery.Scan([(_folder, 2)]);

		// Assert
		Assert.Equal([Path.Combine(_folder, "work", "shop")], found);
		Assert.Contains(Path.Combine(_folder, "work", "deep", "er", "app"), GitDiscovery.Scan([(_folder, 4)]));
	}

	[Fact]
	public void RootOf_LooksUpward_AndCachesTheWay()
	{
		// Arrange
		var root = Repository("shop");
		var deep = Path.Combine(root, "src", "Cart");
		Directory.CreateDirectory(deep);
		var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

		// Act
		var found = GitDiscovery.RootOf(deep, cache);

		// Assert
		Assert.Equal(root, found);
		Assert.Equal(root, cache[Path.Combine(root, "src")]);
		Assert.Null(GitDiscovery.RootOf(_folder, cache));
	}

	[Fact]
	public void ProcessDirectory_OfThisProcess_IsTheCurrentFolder()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Act
		var folder = ProcessDirectory.TryRead(Environment.ProcessId);

		// Assert
		Assert.Equal(Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory), Path.TrimEndingDirectorySeparator(folder ?? string.Empty), ignoreCase: true);
	}

	[Fact]
	public void Poll_AProcessWorkingInARepository_AddsIt_AsActive()
	{
		Assert.SkipUnless(ProcessSnapshot.IsSupported, "Needs a 64-bit process.");

		// Arrange: a process that waits in a repository nobody named.
		var root = Repository("shop");
		using var waiting = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false })!;
		try
		{
			var monitor = GitMonitor.Discovering(Path.GetTempPath());
			var snapshot = new ProcessSnapshot();
			Thread.Sleep(300);

			// Act
			var activity = monitor.Poll(1000, TimeProvider.System.GetUtcNow(), [], snapshot.Take());

			// Assert
			var shop = Assert.Single(activity.Repositories, repository => string.Equals(repository.Root, root, StringComparison.OrdinalIgnoreCase));
			Assert.True(shop.Active);
			Assert.True(shop.IsShown);
		}
		finally
		{
			waiting.Kill(entireProcessTree: true);
		}
	}

	[Fact]
	public void Panel_ManyRepositories_ShowsTheActiveOnes_AndCountsTheRest()
	{
		// Arrange
		var now = TimeProvider.System.GetUtcNow();
		GitRepositoryStatus[] repositories =
		[
			new("Resting", "a", "main", null, 0, 0, 0, null, null, null, null, StatusKnown: false, Active: false),
			new("Busy", "b", "main", null, 2, 10, 1, null, null, null, null, StatusKnown: true, Active: true, LastActivity: now),
			new("Dirty", "c", "dev", null, 1, 3, 0, null, null, null, null, StatusKnown: true, Active: false, LastActivity: now.AddHours(-1)),
		];

		// Act
		var lines = InfoPanel.Build(null, new MetricSnapshot { Time = 1, Git = new GitActivity(repositories) }, null);

		// Assert
		var names = lines.Where(line => line.Kind == PanelLineKind.Field).Select(line => line.Label).ToList();
		Assert.Equal(["Busy", "Dirty"], names);
		Assert.Contains(lines, line => line is { Kind: PanelLineKind.Note, Text: "1 more followed" });
	}

	private string Repository(params string[] parts)
	{
		var path = Path.Combine([_folder, .. parts]);
		Directory.CreateDirectory(Path.Combine(path, ".git"));
		return path;
	}
}
