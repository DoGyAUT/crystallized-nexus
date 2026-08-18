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
	/// The running executable cannot be overwritten on Windows, but it can be renamed.
	/// The previous build is therefore left beside the new one and swept up on next start.
	/// </summary>
	public static void CleanUpAfterUpdate()
	{
		try
		{
			var retired = Environment.ProcessPath + RetiredSuffix;
			if (File.Exists(retired))
				File.Delete(retired);
		}
		catch (Exception)
		{
			// Still held by the exiting process, or read-only; it will be retried next start.
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

	/// <summary>The asset name the launcher workflow publishes for this platform.</summary>
	public static string ExpectedAssetName
		=> "CNLauncher-" + RuntimeInformation.RuntimeIdentifier + (OperatingSystem.IsWindows() ? ".exe" : "");

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

		var retiredPath = currentPath + RetiredSuffix;

		try
		{
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
		catch (Exception)
		{
			return false;
		}
	}
}
