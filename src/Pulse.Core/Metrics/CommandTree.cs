using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>A process a measured command started that was still running when the command ended.</summary>
/// <param name="Name">Its name, without ".exe".</param>
/// <param name="Id">Its process id.</param>
/// <param name="Bytes">Its private memory.</param>
public sealed record LeftProcess(string Name, int Id, long Bytes);

/// <summary>
/// The processes a command measured with <c>tpulse -- command</c> started, at any depth, followed while it runs, so that
/// what it left running when it ended can be named: an MSBuild node kept for reuse, a compiler server, a test host, a
/// dev server started in the background. A snapshot of every process is taken about once a second; a process counts
/// when its parent is one of the command's and was created before it, because process ids are reused. That holds when
/// the parent has ended since: a build that started a server and ended still owns the server.
/// </summary>
public sealed class CommandTree
{
	private readonly Lock _gate = new();
	private readonly ProcessSnapshot? _snapshot = ProcessSnapshot.IsSupported ? new ProcessSnapshot() : null;

	/// <summary>The processes of the command, the ones that ended included: creation time by process id.</summary>
	private readonly Dictionary<int, long> _members = [];

	private int _rootId = -1;
	private long _rootCreated = -1;

	/// <summary>Starts following the command, the process <paramref name="rootId"/>, and takes the first snapshot.</summary>
	public void Start(int rootId)
	{
		lock (_gate)
		{
			_rootId = rootId;
		}

		Sample();
	}

	/// <summary>Takes a snapshot and adds the processes started since the last one; nothing before <see cref="Start"/>.</summary>
	public void Sample()
	{
		lock (_gate)
		{
			if (_snapshot is not null && _rootId >= 0)
			{
				Follow(_snapshot.Take(), _rootId, ref _rootCreated, _members);
			}
		}
	}

	/// <summary>
	/// The processes of the command that run now, the command itself left out: after it ended, what it left running,
	/// most memory first.
	/// </summary>
	public List<LeftProcess> Running()
	{
		lock (_gate)
		{
			var running = new List<LeftProcess>();
			if (_snapshot is null || _rootId < 0)
			{
				return running;
			}

			var records = _snapshot.Take();
			Follow(records, _rootId, ref _rootCreated, _members);
			foreach (ref readonly var record in records)
			{
				if (record.Id != _rootId && _members.TryGetValue(record.Id, out var created) && created == record.CreateTime)
				{
					running.Add(new LeftProcess(TopProcesses.Clean(_snapshot.NameOf(record)), record.Id, record.PrivateBytes));
				}
			}

			running.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
			return running;
		}
	}

	/// <summary>
	/// Adds to <paramref name="members"/> every process of <paramref name="records"/> whose parent is a member created
	/// before it, the root (<paramref name="rootId"/>) first. Records go oldest first, so a child started since the last
	/// snapshot is found in the same pass as its parent. A member whose id now belongs to a process created at another
	/// time has ended, and is forgotten, so the new owner of the id is not taken for it.
	/// </summary>
	internal static void Follow(ReadOnlySpan<ProcessRecord> records, int rootId, ref long rootCreated, Dictionary<int, long> members)
	{
		var created = new long[records.Length];
		var order = new int[records.Length];
		for (var i = 0; i < records.Length; i++)
		{
			created[i] = records[i].CreateTime;
			order[i] = i;
		}

		Array.Sort(created, order);
		foreach (var index in order)
		{
			ref readonly var record = ref records[index];
			if (members.TryGetValue(record.Id, out var known))
			{
				if (known == record.CreateTime)
				{
					continue;
				}

				members.Remove(record.Id);
			}

			if (record.Id == rootId && rootCreated < 0)
			{
				rootCreated = record.CreateTime;
				members[record.Id] = record.CreateTime;
				continue;
			}

			if (record.ParentId != record.Id && members.TryGetValue(record.ParentId, out var parent) && parent <= record.CreateTime)
			{
				members[record.Id] = record.CreateTime;
			}
		}
	}
}
