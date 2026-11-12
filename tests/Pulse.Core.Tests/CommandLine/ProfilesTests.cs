using Pulse.Core.CommandLine;

namespace Pulse.Core.Tests.CommandLine;

public sealed class ProfilesTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"), "profiles.json");

	public void Dispose()
	{
		var directory = Path.GetDirectoryName(_path)!;
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Load_NoFile_StartsWithTheSamples()
	{
		// Act
		var profiles = new Profiles(_path).Load();

		// Assert
		Assert.True(File.Exists(_path));
		Assert.Equal(Profiles.Samples.Keys.Order(), profiles.Keys.Order());
		Assert.All(profiles.Values, options => Assert.True(LaunchOptions.TryParse(options, out _, out var error), error));
	}

	[Fact]
	public void Save_ThenLoad_KeepsTheOptions_IgnoringCase()
	{
		// Arrange
		var profiles = new Profiles(_path);

		// Act
		profiles.Save("Mine", ["-cpu", "-counter", @"\Memory\Available MBytes"]);

		// Assert
		Assert.Equal(["-cpu", "-counter", @"\Memory\Available MBytes"], profiles.Load()["mine"]);
	}

	[Fact]
	public void TryExpand_ReplacesTheProfile_AndKeepsTheOtherOptions()
	{
		// Arrange
		var profiles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["dev"] = ["-cpu", "-git"] };

		// Act
		var expanded = Profiles.TryExpand(["-profile", "DEV", "-record"], profiles, out var args, out var error);

		// Assert
		Assert.True(expanded, error);
		Assert.Equal(["-cpu", "-git", "-record"], args);
	}

	[Fact]
	public void TryExpand_UnknownProfile_NamesTheKnownOnes()
	{
		// Act
		var expanded = Profiles.TryExpand(["-profile=nope"], Profiles.Samples, out _, out var error);

		// Assert
		Assert.False(expanded);
		Assert.Contains("nope", error);
		Assert.Contains("dev", error);
	}

	[Theory]
	[InlineData("-profile", "profile", true, null)]
	[InlineData("--profile=dev", "profile", true, "dev")]
	[InlineData("/PROFILES", "profiles", true, null)]
	[InlineData("profile", "profile", false, null)]
	[InlineData("-profiles", "profile", false, null)]
	public void IsOption_MatchesDashesSlashesAndInlineValues(string arg, string name, bool expected, string? value)
	{
		// Act
		var matched = Profiles.IsOption(arg, name, out var inline);

		// Assert
		Assert.Equal(expected, matched);
		if (expected)
		{
			Assert.Equal(value, inline);
		}
	}
}
