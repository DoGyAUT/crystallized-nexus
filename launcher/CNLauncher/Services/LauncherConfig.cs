using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNLauncher.Services;

/// <summary>
/// Persisted launcher settings. Deliberately stored in the user's config directory rather
/// than next to the executable, so the launcher can be moved (or re-downloaded from ModDB)
/// without losing track of an existing installation.
/// </summary>
public sealed class LauncherConfig
{
	public string? InstallDir { get; set; }
	public ReleaseChannel Channel { get; set; } = ReleaseChannel.Playtest;

	/// <summary>
	/// Tag of a specific build the user pinned, or null to follow whatever is newest in
	/// the channel. Pinning is what lets a tester drop back to an earlier build to work
	/// out which one introduced a problem.
	/// </summary>
	public string? PinnedRelease { get; set; }

	[JsonIgnore]
	public bool IsFirstRun => string.IsNullOrWhiteSpace(InstallDir);

	public static string ConfigDir { get; } = ResolveConfigDir();
	public static string ConfigFile { get; } = Path.Combine(ConfigDir, "launcher.json");

	/// <summary>Where the game is installed by default, offered as the pre-filled path on first run.</summary>
	public static string DefaultInstallDir { get; } = Path.Combine(ConfigDir, "Game");

	public static LauncherConfig Load()
	{
		try
		{
			if (File.Exists(ConfigFile))
			{
				var config = JsonSerializer.Deserialize(File.ReadAllText(ConfigFile), ConfigJson.Default.LauncherConfig);
				if (config != null)
					return config;
			}
		}
		catch (Exception)
		{
			// A corrupt config must never block the launcher from starting; falling back
			// to defaults just means the user picks their install directory again.
		}

		return MigrateLegacyInstall() ?? new LauncherConfig();
	}

	public void Save()
	{
		Directory.CreateDirectory(ConfigDir);
		File.WriteAllText(ConfigFile, JsonSerializer.Serialize(this, ConfigJson.Default.LauncherConfig));
	}

	/// <summary>
	/// The pre-GUI launcher installed into "game" next to its own executable and tracked
	/// the version in "version.txt". Adopting that layout saves existing testers a full
	/// re-download.
	/// </summary>
	static LauncherConfig? MigrateLegacyInstall()
	{
		try
		{
			var legacyGameDir = Path.Combine(AppContext.BaseDirectory, "game");
			var legacyVersionFile = Path.Combine(AppContext.BaseDirectory, "version.txt");
			if (!Directory.Exists(legacyGameDir) || !File.Exists(legacyVersionFile))
				return null;

			var config = new LauncherConfig { InstallDir = legacyGameDir };
			GameInstall.WriteInstalledVersion(legacyGameDir, File.ReadAllText(legacyVersionFile).Trim());
			config.Save();
			return config;
		}
		catch (Exception)
		{
			return null;
		}
	}

	static string ResolveConfigDir()
	{
		// LocalApplicationData rather than ApplicationData: the default install lives under
		// this directory, and a roaming profile must not try to sync a gigabyte of game
		// files between machines.
		if (OperatingSystem.IsWindows())
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrystallizedNexus");

		if (OperatingSystem.IsMacOS())
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				"Library", "Application Support", "CrystallizedNexus");

		var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		if (string.IsNullOrEmpty(xdgConfigHome))
			xdgConfigHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

		return Path.Combine(xdgConfigHome, "crystallized-nexus");
	}
}

/// <summary>
/// Source-generated serialisation for <see cref="LauncherConfig"/>. The launcher is
/// published with full trimming, under which reflection-based JSON is not guaranteed to
/// survive; a generated context keeps it correct without preserving the whole serialiser.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LauncherConfig))]
internal sealed partial class ConfigJson : JsonSerializerContext;
