namespace Pulse.Core.Interop;

/// <summary>
/// The current folder of another process, from the <c>/proc/[pid]/cwd</c> link: a shell, an editor, a build or an agent
/// working in a repository tells Pulse where that repository is. Readable for the processes of the same user.
/// </summary>
internal static class ProcessDirectory
{
	/// <summary>The current folder of the process, or null when it cannot be read (another user, gone, folder deleted).</summary>
	public static string? TryRead(int processId)
	{
		try
		{
			var target = new DirectoryInfo($"/proc/{processId}/cwd").LinkTarget;
			return target is null || target.EndsWith(" (deleted)", StringComparison.Ordinal) ? null : target;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
