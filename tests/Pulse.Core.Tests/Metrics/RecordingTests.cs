using Pulse.Core.Metrics;

namespace Pulse.Core.Tests.Metrics;

/// <summary>A recording written by <see cref="CsvRecorder"/> plays back with its values, extra graphs and events.</summary>
public sealed class RecordingTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"), "run.csv");

	public void Dispose()
	{
		var directory = Path.GetDirectoryName(_path)!;
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Load_WhatWasRecorded_PlaysBack()
	{
		// Arrange
		WatchSpec[] watches = [new(WatchKind.Process, "devenv"), new(WatchKind.Counter, @"\Memory\Available MBytes")];
		using (var recorder = new CsvRecorder(_path, watches))
		{
			recorder.Write(Sample(cpu: 10, events: []));
			Thread.Sleep(1100);
			recorder.Write(Sample(cpu: 20, events: [new PulseEvent(2, TimeProvider.System.GetUtcNow(), EventKind.Git, "Pulse", "commit", "Fix, \"quoted\"")]));
		}

		// Act
		var recording = Recording.Load(_path);

		// Assert
		Assert.Equal(2, recording.Samples.Count);
		Assert.Equal(0, recording.Samples[0].Time);
		Assert.InRange(recording.Samples[1].Time, 1, 3);
		Assert.Equal([10.0, 20.0], recording.Samples.Select(sample => sample.CpuPercent));
		Assert.Equal(50, recording.Samples[1].MemoryPercent);
		Assert.Equal(8L * 1024 * 1024 * 1024, recording.Samples[1].MemoryTotalBytes, 1e3);
		Assert.Equal(120, recording.Samples[1].GitChangedLines);
		Assert.Equal([WatchKind.Process, WatchKind.Counter], recording.Info.Watches.Select(watch => watch.Kind));
		Assert.Equal("devenv", recording.Info.Watches[0].Title);
		Assert.Equal(12.5, recording.Samples[1].Watches[0].Value);
		Assert.Equal(8123, recording.Samples[1].Watches[1].Value);
		var replayed = Assert.Single(recording.Samples[1].Events);
		Assert.Equal(("commit", "Pulse", "Fix, \"quoted\"", EventKind.Git), (replayed.Action, replayed.Source, replayed.Text, replayed.Kind));
		Assert.True(recording.Info.HasCpu);
		Assert.False(recording.Info.HasGpu);
		Assert.Equal(["recorded"], recording.Info.GitRepositories);
	}

	[Fact]
	public void Load_NotARecording_SaysSo()
	{
		// Arrange
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.WriteAllText(_path, "name,value\nx,1\n");

		// Act
		var exception = Assert.Throws<InvalidDataException>(() => Recording.Load(_path));

		// Assert
		Assert.Contains("not a Pulse recording", exception.Message);
	}

	[Theory]
	[InlineData("a,b,c", new[] { "a", "b", "c" })]
	[InlineData("a,\"b, c\",d", new[] { "a", "b, c", "d" })]
	[InlineData("\"say \"\"hi\"\"\",", new[] { "say \"hi\"", "" })]
	public void Split_HandlesQuotes(string line, string[] expected)
	{
		// Act & Assert
		Assert.Equal(expected, Recording.Split(line));
	}

	private static MetricSnapshot Sample(double cpu, PulseEvent[] events) => new()
	{
		Time = 1,
		CpuPercent = cpu,
		MemoryPercent = 50,
		MemoryTotalBytes = 8L * 1024 * 1024 * 1024,
		GitChangedLines = 120,
		Events = events,
		Watches = [new WatchReading(12.5, 300 * 1024 * 1024, 3, 2, 40, 900), WatchReading.OfCounter(8123, 1)],
	};
}
