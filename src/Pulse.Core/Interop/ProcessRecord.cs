namespace Pulse.Core.Interop;

/// <summary>One process from a <see cref="ProcessSnapshot"/>.</summary>
internal struct ProcessRecord
{
	public int Id;
	public int ParentId;

	/// <summary>Creation time in 100 ns units; tells a reused process id from the process it replaced.</summary>
	public long CreateTime;

	/// <summary>User plus kernel time in 100 ns units.</summary>
	public long CpuTime;

	/// <summary>Private working set in bytes: the "Memory" column of Task Manager.</summary>
	public long PrivateBytes;

	public int Threads;

	public int Handles;

	/// <summary>Bytes read and written by the process since it started: files, pipes and network.</summary>
	public long IoBytes;

	/// <summary>Where the image name lies in the snapshot buffer, read with <see cref="ProcessSnapshot.NameOf"/>.</summary>
	public int NameOffset;

	public int NameLength;

	/// <summary>Whether the image is Claude: the desktop app, its helpers, or a Claude Code engine (claude.exe on Windows; claude and claude-desktop on Linux).</summary>
	public bool IsClaude;

	/// <summary>The index of the watched name (<see cref="ProcessSnapshot.WatchedNames"/>) the image matches; -1 for none.</summary>
	public int Watch;
}
