using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>The runs of <c>tpulse -- command</c> kept, and a run compared with the ones before it.</summary>
public sealed class RunHistoryTests : IDisposable
{
	private const string Folder = "/src/shop";
	private const string Build = "dotnet build";

	private readonly string _path = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"), "runs.jsonl");

	public void Dispose()
	{
		var folder = Path.GetDirectoryName(_path)!;
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void Append_ThenLoad_KeepsEveryField_NotMeasuredIncluded()
	{
		// Arrange
		var run = Run(72.5, commit: new string('a', 40)) with { CpuAverage = double.NaN };

		// Act
		RunHistory.Append(_path, run);
		RunHistory.Append(_path, Run(60));
		var loaded = RunHistory.Load(_path);

		// Assert
		Assert.Equal(2, loaded.Count);
		Assert.Equal(run, loaded[0]);
		Assert.Equal("aaaaaaa", loaded[0].ShortCommit);
	}

	[Fact]
	public void Load_ALineThatCannotBeRead_IsLeftOut()
	{
		// Arrange
		RunHistory.Append(_path, Run(60));
		File.AppendAllText(_path, "{\"at\": \n");
		RunHistory.Append(_path, Run(61));

		// Act
		var loaded = RunHistory.Load(_path);

		// Assert
		Assert.Equal([60.0, 61.0], loaded.Select(run => run.Seconds));
		Assert.Empty(RunHistory.Load(Path.Combine(Path.GetDirectoryName(_path)!, "none.jsonl")));
	}

	[Fact]
	public void Compare_NoEarlierRunOfThatCommandHere_IsNull()
	{
		// Arrange
		RunRecord[] earlier = [Run(50) with { Folder = "/src/other" }, Run(50) with { Command = "dotnet test" }, Run(50) with { ExitCode = 1 }];

		// Act & Assert
		Assert.Null(RunHistory.Compare(earlier, Run(60)));
	}

	[Fact]
	public void Compare_SlowerThanTheAverage_SaysHowMuch()
	{
		// Arrange
		RunRecord[] earlier = [Run(50), Run(60), Run(10) with { ExitCode = 2 }];

		// Act
		var line = RunHistory.Compare(earlier, Run(66));

		// Assert
		Assert.NotNull(line);
		Assert.StartsWith("  Before    avg 55 s over 2 runs (fastest 50 s, last 1 min)", line, StringComparison.Ordinal);
		Assert.EndsWith("this run 20% slower", line, StringComparison.Ordinal);
	}

	[Fact]
	public void Compare_OneEarlierRunAtAnotherCommit_NamesTheCommit()
	{
		// Arrange
		RunRecord[] earlier = [Run(50, commit: "1234567890abcdef1234567890abcdef12345678")];

		// Act
		var line = RunHistory.Compare(earlier, Run(49, commit: new string('f', 40)));

		// Assert
		Assert.Equal("  Before    50 s the run before at 1234567 · this run about as fast", line);
	}

	[Fact]
	public void Compare_Faster_SaysFaster()
		=> Assert.EndsWith("this run 50% faster", RunHistory.Compare([Run(100)], Run(50)), StringComparison.Ordinal);

	[Fact]
	public void List_LatestFirst_TheFolderOnlyWhenEveryFolderIsListed()
	{
		// Arrange
		RunRecord[] runs = [Run(50) with { Command = "first" }, Run(60) with { Command = "second" }, Run(70) with { Folder = "/src/other", Command = "elsewhere" }];

		// Act
		var here = RunHistory.List(runs, Folder, 10);
		var everywhere = RunHistory.List(runs, null, 2);

		// Assert
		Assert.Equal(2, here.Count);
		Assert.EndsWith("second", here[0], StringComparison.Ordinal);
		Assert.EndsWith("first", here[1], StringComparison.Ordinal);
		Assert.Equal(2, everywhere.Count);
		Assert.Contains("other", everywhere[0], StringComparison.Ordinal);
	}

	[Fact]
	public void Repeated_NamesAverageFastestAndSlowest()
		=> Assert.Equal("tpulse: 3 runs of dotnet build: avg 50 s, fastest 40 s, slowest 1 min", RunHistory.Repeated(Build, [40, 50, 60]));

	private static RunRecord Run(double seconds, string? commit = null)
		=> new(TimeProvider.System.GetUtcNow(), Folder, Build, seconds, 0, commit, 40, 8e9, 1e6, 2e6);
}
