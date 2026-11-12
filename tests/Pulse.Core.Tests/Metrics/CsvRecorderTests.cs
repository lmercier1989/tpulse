using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

public sealed class CsvRecorderTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	[Fact]
	public void Write_Sample_AddsOneRowUnderTheHeader()
	{
		// Arrange
		var path = Path.Combine(_directory, "run.csv");
		var sample = new MetricSnapshot
		{
			Time = 1,
			CpuPercent = 42.12345,
			ClaudeCpuPercent = 3.5,
			ClaudeTokens = new TokenCounts(10, 5, 1000, 70),
		};

		// Act
		using (var recorder = new CsvRecorder(path))
		{
			recorder.Write(sample);
		}

		// Assert
		var lines = File.ReadAllLines(path);
		Assert.Equal(2, lines.Length);
		var header = lines[0].Split(',');
		var row = lines[1].Split(',');
		Assert.Equal(header.Length, row.Length);
		Assert.Equal("42.123", row[Array.IndexOf(header, "cpu_pct")]);
		Assert.Equal("3.5", row[Array.IndexOf(header, "claude_cpu_pct")]);
		Assert.Equal("70", row[Array.IndexOf(header, "tokens_out")]);
		Assert.Equal("1000", row[Array.IndexOf(header, "tokens_cache_read")]);
		Assert.Equal(string.Empty, row[Array.IndexOf(header, "gpu_pct")]);
	}

	[Fact]
	public void DefaultPath_IsATimestampedFileInDocuments()
	{
		// Act
		var path = CsvRecorder.DefaultPath();

		// Assert
		Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);
		Assert.Matches(@"pulse-\d{8}-\d{6}\.csv$", path);
	}
}
