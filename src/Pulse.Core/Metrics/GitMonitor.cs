using System.Collections.Concurrent;

using Pulse.Core.Diagnostics;
using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>
/// The git repositories Pulse follows: their state every sample, and their reflog lines as events on the time axis.
/// Named with <c>-git D:\Repos</c>, it follows exactly those. Otherwise it finds them itself: the repository of the
/// current folder, a scan of the usual places every 10 minutes, and every 10 seconds the folders processes work in (a
/// shell, an editor, a build, an agent), which also marks those repositories as in use.
/// </summary>
internal sealed class GitMonitor
{
	/// <summary>At most this many repositories named with <c>-git</c>.</summary>
	public const int MaximumRepositories = 8;

	/// <summary>At most this many repositories found on their own.</summary>
	public const int MaximumDiscovered = 200;

	private const double ScanSeconds = 600;
	private const double ProcessSeconds = 10;
	private const int AddedPerPoll = 20;

	private readonly List<GitRepository> _repositories = [];
	private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<(ReflogEntry Entry, string Source)> _entries = [];
	private readonly ConcurrentQueue<string> _found = new();
	private readonly Dictionary<string, string?> _rootCache = new(StringComparer.OrdinalIgnoreCase);
	private Dictionary<(int Id, long Created), string?> _processRoots = [];
	private Dictionary<(int Id, long Created), string?> _seen = [];
	private readonly bool _discover;
	private Task? _scan;
	private double _nextScan;
	private double _nextProcesses;

	public GitMonitor(IReadOnlyList<GitRepository> repositories, bool discover = false)
	{
		_discover = discover;
		foreach (var repository in repositories)
		{
			Add(repository);
		}
	}

	public IReadOnlyList<GitRepository> Repositories => _repositories;

	/// <summary>Whether repositories are found on their own rather than named.</summary>
	public bool Discovers => _discover;

	/// <summary>A monitor that finds the repositories itself, starting with the one around <paramref name="folder"/>.</summary>
	public static GitMonitor Discovering(string folder)
	{
		var monitor = new GitMonitor([], discover: true);
		if (GitDiscovery.RootOf(folder, monitor._rootCache) is { } root)
		{
			monitor.Add(root, MonotonicClock.Now); // Pulse was started in it: in use.
		}

		return monitor;
	}

	/// <summary>
	/// The repositories of every <c>-git</c> folder (a folder in a repository, or a folder of repositories), each once,
	/// at most <see cref="MaximumRepositories"/>.
	/// </summary>
	public static IReadOnlyList<GitRepository> Find(IReadOnlyList<string> paths)
	{
		var found = new List<GitRepository>();
		foreach (var path in paths)
		{
			foreach (var repository in GitRepository.Find(path))
			{
				if (found.Count < MaximumRepositories && !found.Any(other => string.Equals(other.Root, repository.Root, StringComparison.OrdinalIgnoreCase)))
				{
					found.Add(repository);
				}
			}
		}

		return found;
	}

	/// <summary>The state of every repository, and the reflog lines written since the last poll as events.</summary>
	/// <param name="now">The sample clock (<see cref="MonotonicClock"/>).</param>
	/// <param name="wallNow">The wall clock at the same moment, to place the events on the sample clock.</param>
	/// <param name="events">Receives the new events.</param>
	/// <param name="processes">Every process of this machine, to find the repositories they work in; may be empty.</param>
	public GitActivity Poll(double now, DateTimeOffset wallNow, List<PulseEvent> events, ReadOnlySpan<ProcessRecord> processes)
	{
		if (_discover)
		{
			Discover(now, wallNow, processes);
		}

		_entries.Clear();
		foreach (var repository in _repositories)
		{
			repository.ReadEvents(_entries, now, _claimed);
			repository.RefreshIfDue(now);
		}

		foreach (var (entry, source) in _entries)
		{
			var time = now - Math.Max(0, (wallNow - entry.At).TotalSeconds);
			events.Add(new PulseEvent(time, entry.At, EventKind.Git, source, entry.Action, entry.Text));
		}

		return new GitActivity([.. _repositories.Select(repository => repository.StatusAt(now))]);
	}

	/// <summary>Adds what the background scan found, starts the next scan when due, and looks at the processes.</summary>
	private void Discover(double now, DateTimeOffset wallNow, ReadOnlySpan<ProcessRecord> processes)
	{
		if (now >= _nextScan && _scan is not { IsCompleted: false })
		{
			_nextScan = now + ScanSeconds;
			_scan = Task.Run(() =>
			{
				try
				{
					foreach (var root in GitDiscovery.Scan(GitDiscovery.UsualRoots()))
					{
						_found.Enqueue(root);
					}
				}
				catch (Exception ex)
				{
					Log.WarnOnce("git-scan", "Looking for git repositories failed.", ex);
				}
			});
		}

		// A first scan can find dozens at once; opening each reads a bit of its reflog, so a few per sample.
		for (var added = 0; added < AddedPerPoll && _found.TryDequeue(out var root); added++)
		{
			Add(root, double.NaN);
		}

		if (now >= _nextProcesses && !processes.IsEmpty)
		{
			_nextProcesses = now + ProcessSeconds;
			LookAtProcesses(now, wallNow, processes);
		}
	}

	/// <summary>
	/// The repositories processes work in. A process's folder is read once; every repository with a process still in it
	/// counts as in use.
	/// </summary>
	private void LookAtProcesses(double now, DateTimeOffset wallNow, ReadOnlySpan<ProcessRecord> processes)
	{
		_seen.Clear();
		var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ref readonly var process in processes)
		{
			if (process.Id <= 4)
			{
				continue; // Idle and System.
			}

			var key = (process.Id, process.CreateTime);
			if (!_processRoots.TryGetValue(key, out var root))
			{
				var folder = ProcessDirectory.TryRead(process.Id);
				root = folder is null ? null : GitDiscovery.RootOf(folder, _rootCache);
			}

			_seen[key] = root;
			if (root is not null)
			{
				inUse.Add(root);
			}
		}

		(_processRoots, _seen) = (_seen, _processRoots);
		foreach (var root in inUse)
		{
			Add(root, now);
			_repositories.FirstOrDefault(repository => string.Equals(repository.Root, root, StringComparison.OrdinalIgnoreCase))?.MarkActive(now, wallNow);
		}
	}

	private void Add(string root, double now)
	{
		if (_roots.Contains(root) || _repositories.Count >= MaximumDiscovered)
		{
			return;
		}

		try
		{
			if (GitRepository.TryOpen(root) is { } repository)
			{
				Add(repository);
				if (!double.IsNaN(now))
				{
					repository.MarkActive(now, TimeProvider.System.GetUtcNow());
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.WarnOnce($"git-open-{root}", $"The repository in {root} cannot be read.", ex);
			_roots.Add(root);
		}
	}

	private void Add(GitRepository repository)
	{
		if (_roots.Add(repository.Root))
		{
			_repositories.Add(repository);
		}
	}
}
