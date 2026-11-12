using System.Text.Json;
using System.Text.Json.Serialization;

using Pulse.Core.Diagnostics;

namespace Pulse.Core.Settings;

/// <summary>Loads and saves <see cref="PulseSettings"/>; a missing or damaged file yields the defaults.</summary>
public sealed class SettingsStore(string filePath)
{
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
	};

	public string FilePath { get; } = filePath;

	public PulseSettings Load()
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return new PulseSettings();
			}

			var settings = JsonSerializer.Deserialize<PulseSettings>(File.ReadAllText(FilePath), Options) ?? new PulseSettings();
			settings.Normalize();
			return settings;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			Log.Warn($"Settings file '{FilePath}' could not be read; defaults are used.", ex);
			return new PulseSettings();
		}
	}

	/// <summary>Writes through a temporary file, so a crash mid-write never leaves a truncated file.</summary>
	public void Save(PulseSettings settings)
	{
		try
		{
			var directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var temporary = FilePath + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
			File.Move(temporary, FilePath, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.Warn($"Settings could not be saved to '{FilePath}'.", ex);
		}
	}
}
