namespace Pulse.Core.Metrics;

/// <summary>
/// Finds git repositories without being told where they are: in the usual places (two folders deep on every fixed
/// drive, such as <c>D:\Repos\Webshop</c>, and three deep in the user folder, such as <c>source\repos\App</c>), and in
/// the folder any process works in. Windows folders, program folders, AppData, hidden folders and links are skipped,
/// and a repository is not looked into, so a scan touches a few thousand folders at most.
/// </summary>
internal static class GitDiscovery
{
	/// <summary>A scan stops after this many folders.</summary>
	public const int MaximumVisits = 6000;

	private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
	{
		"Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information",
		"Recovery", "PerfLogs", "AppData", "Users", "node_modules", "bin", "obj", "packages", "$WinREAgent", "MSOCache",
	};

	/// <summary>The places repositories usually live, with how many folders deep to look.</summary>
	public static IEnumerable<(string Root, int Depth)> UsualRoots()
	{
		foreach (var drive in DriveInfo.GetDrives())
		{
			if (drive.DriveType == DriveType.Fixed && drive.IsReady)
			{
				yield return (drive.RootDirectory.FullName, 2);
			}
		}

		var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (!string.IsNullOrEmpty(profile))
		{
			yield return (profile, 3);
		}
	}

	/// <summary>The working trees up to <c>Depth</c> folders under each root, breadth first.</summary>
	public static List<string> Scan(IEnumerable<(string Root, int Depth)> roots, int maximumVisits = MaximumVisits)
	{
		var found = new List<string>();
		var queue = new Queue<(string Folder, int Depth)>(roots);
		var visits = 0;
		while (queue.Count > 0 && visits < maximumVisits)
		{
			var (folder, depth) = queue.Dequeue();
			visits++;
			List<DirectoryInfo> children;
			try
			{
				children = [.. new DirectoryInfo(folder).EnumerateDirectories()];
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				continue;
			}

			foreach (var child in children)
			{
				if (Skipped.Contains(child.Name)
					|| child.Name.StartsWith('.')
					|| (child.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
				{
					continue;
				}

				if (IsWorkingTree(child.FullName))
				{
					found.Add(child.FullName);
				}
				else if (depth > 1)
				{
					queue.Enqueue((child.FullName, depth - 1));
				}
			}
		}

		return found;
	}

	/// <summary>The working tree <paramref name="folder"/> lies in, looking upward; every folder on the way is cached.</summary>
	public static string? RootOf(string folder, Dictionary<string, string?> cache)
	{
		var visited = new List<string>();
		string? root = null;
		for (var current = TrimSeparator(folder); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
		{
			if (cache.TryGetValue(current, out root))
			{
				break;
			}

			visited.Add(current);
			if (IsWorkingTree(current))
			{
				root = current;
				break;
			}
		}

		foreach (var folderOnTheWay in visited)
		{
			cache[folderOnTheWay] = root;
		}

		return root;
	}

	public static bool IsWorkingTree(string folder)
	{
		var dotGit = Path.Combine(folder, ".git");
		return Directory.Exists(dotGit) || File.Exists(dotGit);
	}

	private static string TrimSeparator(string folder)
		=> folder.Length > 3 ? folder.TrimEnd('\\', '/') : folder;
}
