using System.Globalization;

namespace Pulse.Core.Metrics;

/// <summary>
/// One line of a reflog (<c>.git/logs/HEAD</c>): git appends one for every move of HEAD, whoever moves it (a shell,
/// an IDE, an agent). <c>&lt;old&gt; &lt;new&gt; Name &lt;email&gt; 1731325843 +0100&#9;commit: Fix the login</c>.
/// </summary>
/// <param name="At">When it happened.</param>
/// <param name="Action">"commit", "amend", "merge", "checkout", "rebase", "pull", "reset", "cherry-pick", ...</param>
/// <param name="Text">The commit message, the branch checked out or merged, or what else the line says.</param>
public sealed record ReflogEntry(DateTimeOffset At, string Action, string Text)
{
	/// <summary>
	/// Parses a reflog line into an event; false for a line that is not one, or for the steps inside a rebase or a
	/// pull with rebase (every replayed commit writes a line; only the start and the end are worth a marker).
	/// </summary>
	public static bool TryParse(string line, out ReflogEntry entry)
	{
		entry = null!;
		var tab = line.IndexOf('\t');
		if (tab < 0)
		{
			return false;
		}

		// The identity part ends with "<seconds> <zone>".
		var identity = line.AsSpan(0, tab).TrimEnd();
		var zoneStart = identity.LastIndexOf(' ');
		if (zoneStart <= 0)
		{
			return false;
		}

		var secondsStart = identity[..zoneStart].LastIndexOf(' ');
		if (secondsStart < 0
			|| !long.TryParse(identity[(secondsStart + 1)..zoneStart], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
		{
			return false;
		}

		var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
		var message = line[(tab + 1)..].Trim();
		var colon = message.IndexOf(": ", StringComparison.Ordinal);
		var head = colon < 0 ? message : message[..colon];
		var rest = colon < 0 ? string.Empty : message[(colon + 2)..];

		// "commit (amend)", "rebase (finish)", "pull --rebase (start)": the step is in parentheses.
		string? step = null;
		var open = head.IndexOf(" (", StringComparison.Ordinal);
		if (open >= 0 && head.EndsWith(')'))
		{
			step = head[(open + 2)..^1];
			head = head[..open];
		}

		var space = head.IndexOf(' ');
		var verb = space < 0 ? head : head[..space];
		var argument = space < 0 ? string.Empty : head[(space + 1)..];
		(string Action, string Text)? parsed = verb switch
		{
			"commit" => (step switch { "amend" => "amend", "merge" => "merge", _ => "commit" }, rest),
			"checkout" => ("checkout", AfterLast(rest, " to ")),
			"merge" => ("merge", argument.Length > 0 ? argument : rest),
			"rebase" or "pull" when step is not null and not ("start" or "finish" or "abort") => null,
			"rebase" => ("rebase", step switch
			{
				"start" => "started",
				"abort" => "aborted",
				_ => "finished " + ShortRef(AfterLast(rest, " to ")),
			}),
			"pull" => ("pull", step is null ? rest : $"rebase {step}ed"),
			"reset" => ("reset", AfterLast(rest, " to ")),
			_ when head.Length == 0 => null,
			_ => (verb, rest.Length > 0 ? rest : argument),
		};

		if (parsed is not { } result)
		{
			return false;
		}

		entry = new ReflogEntry(at, result.Action, result.Text.Trim());
		return true;
	}

	private static string AfterLast(string text, string marker)
	{
		var index = text.LastIndexOf(marker, StringComparison.Ordinal);
		return index < 0 ? text : text[(index + marker.Length)..];
	}

	private static string ShortRef(string reference)
		=> reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
}
