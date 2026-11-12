using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class ReflogEntryTests
{
	private const string Identity = "1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 Ada Lovelace <ada@example.org> 1731325843 +0100";

	[Theory]
	[InlineData("commit: Fix the login", "commit", "Fix the login")]
	[InlineData("commit (initial): First", "commit", "First")]
	[InlineData("commit (amend): Fix the login, really", "amend", "Fix the login, really")]
	[InlineData("commit (merge): Merge branch 'x'", "merge", "Merge branch 'x'")]
	[InlineData("checkout: moving from main to feature/login", "checkout", "feature/login")]
	[InlineData("merge feature/login: Fast-forward", "merge", "feature/login")]
	[InlineData("reset: moving to HEAD~1", "reset", "HEAD~1")]
	[InlineData("pull: Fast-forward", "pull", "Fast-forward")]
	[InlineData("pull --rebase (finish): returning to refs/heads/main", "pull", "rebase finished")]
	[InlineData("rebase (start): checkout main", "rebase", "started")]
	[InlineData("rebase (finish): returning to refs/heads/feature/login", "rebase", "finished feature/login")]
	[InlineData("rebase (abort): updating HEAD", "rebase", "aborted")]
	[InlineData("cherry-pick: Fix the logout", "cherry-pick", "Fix the logout")]
	public void TryParse_KnownMessages_GiveActionAndText(string message, string action, string text)
	{
		// Act
		var parsed = ReflogEntry.TryParse($"{Identity}\t{message}", out var entry);

		// Assert
		Assert.True(parsed);
		Assert.Equal(action, entry.Action);
		Assert.Equal(text, entry.Text);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1731325843), entry.At);
	}

	[Theory]
	[InlineData("rebase (pick): Fix the login")]
	[InlineData("rebase (continue): Fix the login")]
	[InlineData("pull --rebase (pick): Fix the login")]
	public void TryParse_StepsInsideARebase_AreSkipped(string message)
	{
		// Act & Assert
		Assert.False(ReflogEntry.TryParse($"{Identity}\t{message}", out _));
	}

	[Theory]
	[InlineData("")]
	[InlineData("not a reflog line")]
	[InlineData("a b Name <mail> notanumber +0100\tcommit: x")]
	public void TryParse_Garbage_IsRejected(string line)
	{
		// Act & Assert
		Assert.False(ReflogEntry.TryParse(line, out _));
	}
}
