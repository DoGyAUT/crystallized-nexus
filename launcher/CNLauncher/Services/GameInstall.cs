using System.Diagnostics;

namespace CNLauncher.Services;

/// <summary>Everything the launcher knows about the installed game: paths, version, state.</summary>
public static class GameInstall
{
	// Windows ships a "winportable" zip (flat folder, CrystallizedNexus.exe at the root).
	// Linux ships a single self-mounting AppImage - no extraction, just chmod +x and run.
	// The AppImage's AppRun script execs "usr/bin/openra-cn" internally, so that's the
	// process name to watch for, not the AppImage filename itself.
	// macOS ships a .dmg disk image containing an "OpenRA - <mod name>.app" bundle; it is
	// copied out under our own fixed name. The bundle's CFBundleExecutable is "Launcher" (a
	// native stub that NSTask-spawns the game and stays running for the whole session), so
	// that is both what we exec and what we watch for.
	public const string WindowsGameExeName = "CrystallizedNexus.exe";
	public const string LinuxGameFileName = "CrystallizedNexus.AppImage";
	public const string LinuxGameProcessName = "openra-cn";
	public const string MacAppBundleName = "CrystallizedNexus.app";
	public const string MacGameProcessName = "Launcher";

	const string VersionFileName = ".cn-version";

	/// <summary>
	/// The installed version is recorded inside the install directory rather than in the
	/// config, so it always travels with the files it describes.
	/// </summary>
	public static string? ReadInstalledVersion(string installDir)
	{
		var file = Path.Combine(installDir, VersionFileName);
		if (!File.Exists(file) || !IsInstalled(installDir))
			return null;

		var version = File.ReadAllText(file).Trim();
		return string.IsNullOrEmpty(version) ? null : version;
	}

	public static void WriteInstalledVersion(string installDir, string version)
		=> File.WriteAllText(Path.Combine(installDir, VersionFileName), version);

	public static string GameExePath(string installDir)
		=> OperatingSystem.IsWindows() ? Path.Combine(installDir, WindowsGameExeName)
			: OperatingSystem.IsMacOS() ? Path.Combine(installDir, MacAppBundleName, "Contents", "MacOS", MacGameProcessName)
			: Path.Combine(installDir, LinuxGameFileName);

	public static bool IsInstalled(string installDir)
	{
		var exe = GameExePath(installDir);
		return OperatingSystem.IsMacOS() ? Directory.Exists(Path.Combine(installDir, MacAppBundleName)) : File.Exists(exe);
	}

	public static bool IsGameRunning()
	{
		try
		{
			if (OperatingSystem.IsWindows())
				return Process.GetProcessesByName("OpenRA").Length > 0
					|| Process.GetProcessesByName("CrystallizedNexus").Length > 0;

			return Process.GetProcessesByName(
				OperatingSystem.IsMacOS() ? MacGameProcessName : LinuxGameProcessName).Length > 0;
		}
		catch (Exception)
		{
			// Process enumeration can fail under sandboxing; assume nothing is running
			// rather than deadlocking the user out of an update.
			return false;
		}
	}

	public static Process Launch(string installDir)
	{
		var exe = GameExePath(installDir);
		if (!IsInstalled(installDir))
			throw new FileNotFoundException($"The game executable is missing from {installDir}.", exe);

		// The packaged launcher (Windows exe / Linux AppImage / macOS .app) already has the
		// mod, engine and search-path arguments baked in - no extra arguments needed here.
		var psi = new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = false };
		return Process.Start(psi) ?? throw new InvalidOperationException("The game process failed to start.");
	}

	public static void Uninstall(string installDir)
	{
		if (Directory.Exists(installDir))
			Directory.Delete(installDir, recursive: true);
	}

	/// <summary>
	/// Mirrors OpenRA's own support-directory resolution (see the engine's Platform.cs) so
	/// the launcher points at the same logs, settings and content the game actually uses.
	/// </summary>
	public static string SupportDir(string installDir)
	{
		// A "Support" folder inside the game directory turns the install portable, and the
		// engine prefers it over the per-user location when present.
		var localSupportDir = Path.Combine(installDir, "Support");
		if (Directory.Exists(localSupportDir))
			return localSupportDir;

		string modern, legacy;
		if (OperatingSystem.IsWindows())
		{
			modern = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA");
			legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "OpenRA");
		}
		else if (OperatingSystem.IsMacOS())
		{
			modern = legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				"Library", "Application Support", "OpenRA");
		}
		else
		{
			var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
			if (string.IsNullOrEmpty(xdgConfigHome))
				xdgConfigHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

			modern = Path.Combine(xdgConfigHome, "openra");
			legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openra");
		}

		return !Directory.Exists(modern) && Directory.Exists(legacy) ? legacy : modern;
	}

	public static string LogsDir(string installDir) => Path.Combine(SupportDir(installDir), "Logs");

	/// <summary>
	/// The mod loads the original Tiberian Sun assets from the support directory (see
	/// mod.yaml's "~^SupportDir|Content/ts"). If they are absent the game runs its own
	/// content installer on first start, which surprises testers unless we say so upfront.
	/// </summary>
	public static bool HasTiberianSunContent(string installDir)
	{
		var contentDir = Path.Combine(SupportDir(installDir), "Content", "ts");
		if (!Directory.Exists(contentDir))
			return false;

		// conquer.mix is one of the base packages listed in mod.yaml's ContentPackages and
		// is present in every complete Tiberian Sun installation.
		return File.Exists(Path.Combine(contentDir, "conquer.mix"));
	}
}
