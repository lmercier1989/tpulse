using Pulse.Core.Processing;
using Pulse.Core.Settings;

namespace Pulse.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
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
	public void Save_ThenLoad_RoundTrips()
	{
		// Arrange
		var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
		var settings = new PulseSettings { HistorySeconds = 300, Compact = true, Window = new WindowBounds { Left = 5, Top = 6, Width = 700, Height = 400 } };
		settings.Channel(MetricKind.Disk).Size = ChannelSize.Collapsed;

		// Act
		store.Save(settings);
		var loaded = store.Load();

		// Assert
		Assert.Equal(300, loaded.HistorySeconds);
		Assert.True(loaded.Compact);
		Assert.Equal(ChannelSize.Collapsed, loaded.Channel(MetricKind.Disk).Size);
		Assert.Equal(700, loaded.Window!.Width);
	}

	[Fact]
	public void Load_MissingFile_ReturnsDefaults()
	{
		// Arrange
		var store = new SettingsStore(Path.Combine(_directory, "missing.json"));

		// Act
		var loaded = store.Load();

		// Assert
		Assert.Equal(60, loaded.HistorySeconds);
		Assert.All(loaded.Channels, channel => Assert.True(channel.Visible));
	}

	[Fact]
	public void Load_DamagedFile_ReturnsDefaults()
	{
		// Arrange
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, "settings.json");
		File.WriteAllText(path, "{ \"HistorySeconds\": ");
		var store = new SettingsStore(path);

		// Act
		var loaded = store.Load();

		// Assert
		Assert.Equal(60, loaded.HistorySeconds);
	}
}
