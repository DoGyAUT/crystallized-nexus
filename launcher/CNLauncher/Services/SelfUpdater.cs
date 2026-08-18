using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CNLauncher.Services;

public sealed record LauncherUpdate(Release Release, ReleaseAsset Asset, Version Version);

/// <summary>
/// Keeps the launcher itself current. Launcher builds are published under "launcher-v*"
/// tags in the same repository as the game builds, so a tester who downloaded the
/// executable once (from ModDB, say) never has to fetch it by hand again.
/// </summary>
public static class SelfUpdater
{
	const string RetiredSuffix = ".old";

	/// <summary>
	/// The replaced build is left beside the new one and swept up on the next start,
	/// because neither platform lets the running launcher delete itself.
	/// </summary>
	public static void CleanUpAfterUpdate()
	{
		try
		{
			var retiredFile = Environment.ProcessPath + RetiredSuffix;
			if (File.Exists(retiredFile))
				File.Delete(retiredFile);

			var bundle = MacAppBundlePath();
			if (bundle != null && Directory.Exists(bundle + RetiredSuffix))
				Directory.Delete(bundle + RetiredSuffix, recursive: true);
		}
		catch (Exception)
		{
			// Still held by the exiting process, or read-only; retried on the next start.
		}
	}

	public static LauncherUpdate? FindUpdate(IReadOnlyList<Release> releases)
	{
		var current = AppVersion.Parse(AppVersion.Current);

		LauncherUpdate? best = null;
		foreach (var release in releases)
		{
			var version = AppVersion.ParseLauncherTag(release.TagName);
			if (version == null || version <= current)
				continue;

			var asset = release.Assets.FirstOrDefault(a =>
				a.Name.Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase));

			if (asset != null && (best == null || version > best.Version))
				best = new LauncherUpdate(release, asset, version);
		}

		return best;
	}

	/// <summary>
	/// The asset name the launcher workflow publishes for this platform. Derived from the
	/// OS and architecture rather than from RuntimeInformation.RuntimeIdentifier, which is
	/// not contractually a portable RID - a host reporting something like
	/// "ubuntu.22.04-x64" would silently never find its own update.
	/// </summary>
	public static string ExpectedAssetName
	{
		get
		{
			var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
			var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

			// macOS ships an .app inside a disk image; the other platforms ship the bare
			// executable. Must match the asset names in .github/workflows/launcher.yml.
			var extension = OperatingSystem.IsWindows() ? ".exe" : OperatingSystem.IsMacOS() ? ".dmg" : "";
			return $"CNLauncher-{os}-{arch}{extension}";
		}
	}

	/// <summary>
	/// Downloads the new build, swaps it in and restarts. Returns false when the swap could
	/// not be performed, in which case the caller carries on with the current build.
	/// </summary>
	public static async Task<bool> ApplyAsync(
		HttpClient http,
		LauncherUpdate update,
		IProgress<DownloadProgress> progress,
		CancellationToken ct)
	{
		var currentPath = Environment.ProcessPath;
		if (string.IsNullOrEmpty(currentPath))
			return false;

		var stagedPath = Path.Combine(LauncherConfig.ConfigDir, "downloads", update.Asset.Name);
		await Downloader.DownloadAsync(http, update.Asset, stagedPath, progress, ct);

		try
		{
			return OperatingSystem.IsMacOS()
				? ReplaceAppBundle(stagedPath)
				: ReplaceExecutable(currentPath, stagedPath);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Windows and Linux ship a single file. A running executable cannot be overwritten on
	/// Windows, but it can be renamed, which is what makes this work without a helper
	/// process or a second launcher.
	/// </summary>
	static bool ReplaceExecutable(string currentPath, string stagedPath)
	{
		var retiredPath = currentPath + RetiredSuffix;

		if (File.Exists(retiredPath))
			File.Delete(retiredPath);

		File.Move(currentPath, retiredPath);

		try
		{
			File.Move(stagedPath, currentPath);
		}
		catch (Exception)
		{
			// Put the working build back rather than leaving the user with no launcher.
			File.Move(retiredPath, currentPath);
			throw;
		}

		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(currentPath,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
				UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
				UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

		Process.Start(new ProcessStartInfo(currentPath) { UseShellExecute = false });
		return true;
	}

	/// <summary>
	/// macOS ships an .app bundle inside a disk image, so the single-file rename does not
	/// apply: the image is mounted, the bundle copied out beside the running one, and the
	/// two swapped. A bundle can be replaced while its process runs because the running
	/// image is already open.
	/// </summary>
	static bool ReplaceAppBundle(string dmgPath)
	{
		var bundle = MacAppBundlePath();
		if (bundle == null)
			return false;

		var mountPoint = Path.Combine(Path.GetTempPath(), $"cn-launcher-update-{Guid.NewGuid():N}");
		var incoming = bundle + ".new";
		Directory.CreateDirectory(mountPoint);

		try
		{
			RunProcess("hdiutil", ["attach", dmgPath, "-mountpoint", mountPoint, "-nobrowse", "-quiet"]);

			var source = Directory.GetDirectories(mountPoint, "*.app").FirstOrDefault()
				?? throw new InvalidOperationException("The disk image does not contain an .app bundle.");

			if (Directory.Exists(incoming))
				Directory.Delete(incoming, recursive: true);

			// ditto rather than a manual copy: it preserves the execute bits, symlinks and
			// extended attributes that the bundle's code signature is computed over.
			RunProcess("ditto", [source, incoming]);
		}
		finally
		{
			TryRunProcess("hdiutil", ["detach", mountPoint, "-quiet"]);
			TryDeleteDirectory(mountPoint);
		}

		// The copy inherits the disk image's quarantine flag, which would make Gatekeeper
		// block the very build that just replaced a running one.
		TryRunProcess("xattr", ["-cr", incoming]);

		var retired = bundle + RetiredSuffix;
		if (Directory.Exists(retired))
			Directory.Delete(retired, recursive: true);

		Directory.Move(bundle, retired);

		try
		{
			Directory.Move(incoming, bundle);
		}
		catch (Exception)
		{
			Directory.Move(retired, bundle);
			throw;
		}

		// "open -n" starts a fresh instance rather than activating the one that is on its
		// way out.
		Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-n", bundle }, UseShellExecute = false });
		return true;
	}

	/// <summary>
	/// The .app directory the running executable lives in, or null when the launcher is
	/// running as a bare binary - in which case there is nothing to swap and the update is
	/// declined rather than guessed at.
	/// </summary>
	static string? MacAppBundlePath()
	{
		if (!OperatingSystem.IsMacOS())
			return null;

		// <bundle>.app/Contents/MacOS/<executable>
		var macOsDir = Path.GetDirectoryName(Environment.ProcessPath);
		var contentsDir = Path.GetDirectoryName(macOsDir);
		var bundle = Path.GetDirectoryName(contentsDir);

		return bundle != null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
			? bundle
			: null;
	}

	static void RunProcess(string fileName, IEnumerable<string> arguments)
	{
		var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
		foreach (var arg in arguments)
			psi.ArgumentList.Add(arg);

		using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
		process.WaitForExit();
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
	}

	static void TryRunProcess(string fileName, IEnumerable<string> arguments)
	{
		try
		{
			RunProcess(fileName, arguments);
		}
		catch (Exception)
		{
			// Best-effort cleanup; a failure here must not mask the original error.
		}
	}

	static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (Exception)
		{
		}
	}
}
