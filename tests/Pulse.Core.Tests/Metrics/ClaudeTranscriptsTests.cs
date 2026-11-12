using System.Text;

using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class ClaudeTranscriptsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"));
	private readonly string _file;

	public ClaudeTranscriptsTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "D--Repos-Pulse"));
		_file = Path.Combine(_root, "D--Repos-Pulse", "session.jsonl");
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void Poll_AnswersWrittenBeforeStart_AreNotCountedButTheSessionIsKnown()
	{
		// Arrange
		Append(Answer("m1", input: 10, cacheRead: 1000, output: 50));

		// Act
		using var transcripts = new ClaudeTranscripts(_root, watch: false);
		var (tokens, sessions) = transcripts.Poll(0);

		// Assert
		Assert.Equal(default, tokens);
		var session = Assert.Single(sessions);
		Assert.Equal("Pulse", session.Project);
		Assert.Equal("main", session.Branch);
		Assert.Equal("claude-opus-5-5", session.Model);
		Assert.Equal(1010 + 5, session.ContextTokens);
	}

	[Fact]
	public void Poll_NewAnswer_IsCountedOnceEvenWhenWrittenPerContentBlock()
	{
		// Arrange
		Append(Answer("m0", output: 1));
		using var transcripts = new ClaudeTranscripts(_root, watch: false);

		// Act: the same answer written three times (thinking, text, tool use), its output growing.
		Append(Answer("m1", input: 10, cacheRead: 1000, output: 20));
		Append(Answer("m1", input: 10, cacheRead: 1000, output: 20));
		Append(Answer("m1", input: 10, cacheRead: 1000, output: 35));
		var (tokens, _) = transcripts.Poll(0);

		// Assert
		Assert.Equal(new TokenCounts(10, 5, 1000, 35), tokens);
		Assert.Equal(tokens, transcripts.Total);
	}

	[Fact]
	public void Poll_SubagentAnswer_CountsTokensButKeepsTheMainContext()
	{
		// Arrange
		Append(Answer("m1", cacheRead: 5000, output: 1));
		using var transcripts = new ClaudeTranscripts(_root, watch: false);

		// Act
		Append(Answer("s1", cacheRead: 300, output: 40, sidechain: true));
		var (tokens, sessions) = transcripts.Poll(0);

		// Assert
		Assert.Equal(40, tokens.Output);
		Assert.Equal(5000 + 10 + 5, Assert.Single(sessions).ContextTokens);
	}

	[Fact]
	public void Poll_SyntheticMessagesAndUserLines_AreIgnored()
	{
		// Arrange
		Append(Answer("m0", output: 1));
		using var transcripts = new ClaudeTranscripts(_root, watch: false);

		// Act
		Append(Answer("x1", output: 99, model: "<synthetic>"));
		Append("""{"type":"user","sessionId":"s","message":{"role":"user","content":"usage"}}""");
		var (tokens, _) = transcripts.Poll(0);

		// Assert
		Assert.Equal(default, tokens);
	}

	[Fact]
	public void Poll_LineStillBeingWritten_IsReadWhenComplete()
	{
		// Arrange
		Append(Answer("m0", output: 1));
		using var transcripts = new ClaudeTranscripts(_root, watch: false);
		var line = Answer("m1", output: 70);

		// Act
		File.AppendAllText(_file, line[..40], Encoding.UTF8);
		var (first, _) = transcripts.Poll(0);
		File.AppendAllText(_file, line[40..] + "\n", Encoding.UTF8);
		var (second, _) = transcripts.Poll(100);

		// Assert
		Assert.Equal(0, first.Output);
		Assert.Equal(70, second.Output);
	}

	private void Append(string line) => File.AppendAllText(_file, line + "\n", Encoding.UTF8);

	private static string Answer(string id, long input = 10, long cacheWrite = 5, long cacheRead = 0, long output = 0, bool sidechain = false, string model = "claude-opus-5-5")
	{
		var time = TimeProvider.System.GetUtcNow().ToString("O");
		return "{\"type\":\"assistant\",\"isSidechain\":" + (sidechain ? "true" : "false")
			   + ",\"sessionId\":\"s1\",\"cwd\":\"D:\\\\Repos\\\\Pulse\",\"gitBranch\":\"main\",\"timestamp\":\"" + time
			   + "\",\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model
			   + "\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"secret\"}],\"usage\":{\"input_tokens\":" + input
			   + ",\"cache_creation_input_tokens\":" + cacheWrite + ",\"cache_read_input_tokens\":" + cacheRead
			   + ",\"output_tokens\":" + output + "}}}";
	}
}
