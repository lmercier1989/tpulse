using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>What the Claude desktop app and its Code sessions cost the machine right now.</summary>
/// <param name="AppCpu">CPU of the app itself (window, renderers, helpers), in % of the whole machine.</param>
/// <param name="SessionsCpu">CPU of the Claude Code engines, one per session.</param>
/// <param name="ToolsCpu">CPU of everything the sessions started: shells, builds, tests, git.</param>
/// <param name="AppBytes">Private memory of the app.</param>
/// <param name="SessionsBytes">Private memory of the engines.</param>
/// <param name="ToolsBytes">Private memory of the tools.</param>
/// <param name="Sessions">Number of engine processes.</param>
/// <param name="Tools">Number of tool processes.</param>
public readonly record struct ClaudeProcessUsage(
	double AppCpu,
	double SessionsCpu,
	double ToolsCpu,
	double AppBytes,
	double SessionsBytes,
	double ToolsBytes,
	int Sessions,
	int Tools)
{
	public static ClaudeProcessUsage None { get; } = new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);

	public double TotalCpu => AppCpu + SessionsCpu + ToolsCpu;

	public double TotalBytes => AppBytes + SessionsBytes + ToolsBytes;

	public bool IsRunning => AppBytes > 0 || SessionsBytes > 0;
}

/// <summary>Which part of Claude a process belongs to.</summary>
internal enum ClaudeRole : byte
{
	None,
	App,
	Session,
	Tool,
}

/// <summary>
/// Splits the load of Claude into the app, its sessions and the tools those sessions start, from a process snapshot.
/// All of them run as claude.exe; the desktop app lives under WindowsApps (or AnthropicClaude), a session engine
/// anywhere else (the app's claude-code folder, or a command-line install). Tools are every process below an engine,
/// at any depth, so a build started by a session counts for Claude. A parent only counts when it was created before
/// its child, because Windows reuses process ids.
/// </summary>
internal sealed class ClaudeProcesses
{
	private const int MaximumDepth = 32;

	private readonly ProcessCpuMeter _meter = new();
	private readonly Dictionary<(int Id, long Created), bool> _engines = [];
	private readonly Dictionary<int, int> _indexById = [];
	private ClaudeRole[] _roles = new ClaudeRole[1024];

	/// <summary>The load of Claude in <paramref name="records"/>, a snapshot of every process of the machine.</summary>
	public ClaudeProcessUsage Sample(ReadOnlySpan<ProcessRecord> records)
	{
		if (records.IsEmpty)
		{
			return ClaudeProcessUsage.None;
		}

		var measured = _meter.Begin();
		if (_roles.Length < records.Length)
		{
			_roles = new ClaudeRole[records.Length * 2];
		}

		var roles = _roles.AsSpan(0, records.Length);
		Classify(records, roles, _indexById, IsEngine);

		double appCpu = 0, sessionsCpu = 0, toolsCpu = 0, appBytes = 0, sessionsBytes = 0, toolsBytes = 0;
		int sessions = 0, tools = 0;
		for (var i = 0; i < records.Length; i++)
		{
			if (roles[i] == ClaudeRole.None)
			{
				continue;
			}

			ref readonly var record = ref records[i];
			var cpu = _meter.Percent(record);
			switch (roles[i])
			{
				case ClaudeRole.App:
					appCpu += cpu;
					appBytes += record.PrivateBytes;
					break;
				case ClaudeRole.Session:
					sessionsCpu += cpu;
					sessionsBytes += record.PrivateBytes;
					sessions++;
					break;
				default:
					toolsCpu += cpu;
					toolsBytes += record.PrivateBytes;
					tools++;
					break;
			}
		}

		_meter.End();
		if (_engines.Count > 512)
		{
			_engines.Clear(); // Forget processes that ended; live ones are looked up again.
		}

		return measured
			? new ClaudeProcessUsage(appCpu, sessionsCpu, toolsCpu, appBytes, sessionsBytes, toolsBytes, sessions, tools)
			: ClaudeProcessUsage.None with { AppBytes = appBytes, SessionsBytes = sessionsBytes, ToolsBytes = toolsBytes, Sessions = sessions, Tools = tools };
	}

	/// <summary>Gives every process its role; <paramref name="isEngine"/> tells an engine from the app for claude.exe.</summary>
	internal static void Classify(ReadOnlySpan<ProcessRecord> records, Span<ClaudeRole> roles, Dictionary<int, int> indexById, Func<ProcessRecord, bool> isEngine)
	{
		indexById.Clear();
		for (var i = 0; i < records.Length; i++)
		{
			indexById[records[i].Id] = i;
			roles[i] = records[i].IsClaude ? (isEngine(records[i]) ? ClaudeRole.Session : ClaudeRole.App) : ClaudeRole.None;
		}

		for (var i = 0; i < records.Length; i++)
		{
			if (records[i].IsClaude)
			{
				continue;
			}

			// Walk up to the first Claude ancestor: below an engine or a tool means a tool, below the app means app.
			var child = i;
			for (var depth = 0; depth < MaximumDepth; depth++)
			{
				if (!indexById.TryGetValue(records[child].ParentId, out var parent)
					|| parent == child
					|| records[parent].CreateTime > records[child].CreateTime)
				{
					break;
				}

				if (roles[parent] != ClaudeRole.None && (records[parent].IsClaude || roles[parent] == ClaudeRole.Tool))
				{
					roles[i] = roles[parent] == ClaudeRole.App ? ClaudeRole.App : ClaudeRole.Tool;
					break;
				}

				child = parent;
			}
		}
	}

	private bool IsEngine(ProcessRecord record)
	{
		var key = (record.Id, record.CreateTime);
		if (_engines.TryGetValue(key, out var engine))
		{
			return engine;
		}

		var path = ProcessSnapshot.ImagePath(record.Id);
		engine = path is not null
				 && !path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
				 && !path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase);
		_engines[key] = engine;
		return engine;
	}
}
