using System.Text.Json;

namespace Pulse.Core.CommandLine;

/// <summary>
/// Named sets of options: <c>tpulse -profile dev</c> stands for the options saved under "dev", and more options may
/// follow it. They live in <c>%LOCALAPPDATA%\Pulse\profiles.json</c> as name and argument list, so they can be edited by
/// hand; <c>-save-profile name</c> saves the rest of a command line, <c>-profiles</c> lists them. The file starts with a
/// few samples.
/// </summary>
public sealed class Profiles
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	private readonly string _path;

	public Profiles(string path) => _path = path;

	/// <summary>The profiles a new profiles file starts with.</summary>
	public static IReadOnlyDictionary<string, string[]> Samples { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
	{
		["claude"] = ["-claude", "-cpu", "-memory", "-git", "-history", "5m"],
		["dev"] = ["-cpu", "-memory", "-disk", "-git", "-process", "dotnet"],
		["server"] = ["-cpu", "-memory", "-disk", "-network", "-history", "5m"],
		["web"] = ["-cpu", "-memory", "-network", "-process", "w3wp", "-counter", @"\Web Service(_Total)\Current Connections"],
		["sql"] = ["-cpu", "-memory", "-disk", "-process", "sqlservr"],
	};

	public string FilePath => _path;

	/// <summary><c>%LOCALAPPDATA%\Pulse\profiles.json</c>.</summary>
	public static string DefaultPath()
		=> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "profiles.json");

	/// <summary>The profiles, by name; a missing file is created with the samples first.</summary>
	public IReadOnlyDictionary<string, string[]> Load()
	{
		if (!File.Exists(_path))
		{
			Write(Samples);
		}

		try
		{
			var loaded = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_path)) ?? [];
			return new Dictionary<string, string[]>(loaded, StringComparer.OrdinalIgnoreCase);
		}
		catch (JsonException ex)
		{
			throw new InvalidDataException($"{_path} is not valid JSON: {ex.Message}", ex);
		}
	}

	/// <summary>Saves <paramref name="arguments"/> under <paramref name="name"/>, replacing a profile of that name.</summary>
	public void Save(string name, IReadOnlyList<string> arguments)
	{
		var profiles = new Dictionary<string, string[]>(Load(), StringComparer.OrdinalIgnoreCase) { [name] = [.. arguments] };
		Write(profiles);
	}

	/// <summary>
	/// Replaces every <c>-profile name</c> (or <c>-profile=name</c>) in <paramref name="args"/> by the options of that
	/// profile; false with a message for a name that does not exist.
	/// </summary>
	public static bool TryExpand(IReadOnlyList<string> args, IReadOnlyDictionary<string, string[]> profiles, out List<string> expanded, out string? error)
	{
		expanded = [];
		error = null;
		for (var i = 0; i < args.Count; i++)
		{
			if (!IsOption(args[i], "profile", out var inline))
			{
				expanded.Add(args[i]);
				continue;
			}

			var name = inline ?? (i + 1 < args.Count ? args[++i] : string.Empty);
			if (!profiles.TryGetValue(name, out var options))
			{
				error = $"There is no profile '{name}'. Profiles: {string.Join(", ", profiles.Keys.Order(StringComparer.OrdinalIgnoreCase))} (tpulse -profiles lists them).";
				return false;
			}

			expanded.AddRange(options);
		}

		return true;
	}

	/// <summary>Whether <paramref name="arg"/> is the option <paramref name="name"/>, with its inline value after = or :.</summary>
	public static bool IsOption(string arg, string name, out string? inlineValue)
	{
		inlineValue = null;
		var token = arg.Trim().TrimStart('-', '/');
		var separator = token.IndexOfAny(['=', ':']);
		if (separator > 0)
		{
			inlineValue = token[(separator + 1)..];
			token = token[..separator];
		}

		return arg.Length > 0 && arg[0] is '-' or '/' && token.Equals(name, StringComparison.OrdinalIgnoreCase);
	}

	private void Write(IReadOnlyDictionary<string, string[]> profiles)
	{
		var directory = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var ordered = profiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(pair => pair.Key, pair => pair.Value);
		File.WriteAllText(_path, JsonSerializer.Serialize(ordered, Json));
	}
}
