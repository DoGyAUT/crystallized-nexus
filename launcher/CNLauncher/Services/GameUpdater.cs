using System.Diagnostics;
using System.IO.Compression;

namespace CNLauncher.Services;

public sealed record InstallCandidate(Release Release, ReleaseAsset Asset);

/// <summary>Picks the right release for this platform and installs it.</summary>
public static class GameUpdater
{
	/// <summary>
	/// Walks the release list newest-first and returns the first entry in the selected
	/// channel that actually carries a package for this platform. A release whose CI run
	/// has not finished (or whose platform job failed) therefore falls back to the newest
	/// release that does have one, instead of leaving the tester with nothing to install.
	/// </summary>
	public static InstallCandidate? SelectCandidate(IReadOnlyList<Release> releases, ReleaseChannel channel)
	{
		foreach (var release in releases.Where(r => channel.Accepts(r)))
		{
			var asset = FindAsset(release);
			if (asset != null)
				return new InstallCandidate(release, asset);
		}

		return null;
	}

	/// <summary>
	/// Resolves one exact tag. Returns null when that release is gone or never had a
	/// package for this platform, which the caller treats as "fall back to latest".
	/// </summary>
	public static InstallCandidate? SelectSpecific(IReadOnlyList<Release> releases, string tagName)
	{
		var release = releases.FirstOrDefault(r => r.TagName == tagName);
		if (release == null)
			return null;

		var asset = FindAsset(release);
		return asset == null ? null : new InstallCandidate(release, asset);
	}

	/// <summary>Every build in the channel that this platform could actually install.</summary>
	public static IReadOnlyList<InstallCandidate> SelectAll(IReadOnlyList<Release> releases, ReleaseChannel channel)
		=> releases.Where(r => channel.Accepts(r))
			.Select(r => (Release: r, Asset: FindAsset(r)))
			.Where(x => x.Asset != null)
			.Select(x => new InstallCandidate(x.Release, x.Asset!))
			.ToList();

	static ReleaseAsset? FindAsset(Release release)
	{
		if (OperatingSystem.IsWindows())
		{
			// The x86 package runs fine on 64-bit Windows, so it beats dropping back to an
			// older release when only the 32-bit job produced an artifact.
			return Match(release, "winportable", "-x64-")
				?? Match(release, "winportable", "-x86-");
		}

		if (OperatingSystem.IsMacOS())
			return release.Assets.FirstOrDefault(a => a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));

		return release.Assets.FirstOrDefault(a => a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));
	}

	static ReleaseAsset? Match(Release release, params string[] fragments)
		=> release.Assets.FirstOrDefault(a =>
			fragments.All(f => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));

	public static async Task InstallAsync(
		HttpClient http,
		InstallCandidate candidate,
		string installDir,
		IProgress<DownloadProgress> downloadProgress,
		Action<string> status,
		CancellationToken ct)
	{
		var downloadPath = Path.Combine(LauncherConfig.ConfigDir, "downloads", candidate.Asset.Name);

		status($"Downloading {candidate.Release.TagName}...");
		await Downloader.DownloadAsync(http, candidate.Asset, downloadPath, downloadProgress, ct);

		try
		{
			status("Installing...");
			await Task.Run(() => Install(downloadPath, installDir), ct);
			GameInstall.WriteInstalledVersion(installDir, candidate.Release.TagName);
		}
		finally
		{
			TryDelete(downloadPath);
		}
	}

	static void Install(string downloadPath, string installDir)
	{
		if (OperatingSystem.IsWindows())
			InstallFromZip(downloadPath, installDir);
		else if (OperatingSystem.IsMacOS())
			InstallFromDmg(downloadPath, installDir);
		else
			InstallAppImage(downloadPath, installDir);
	}

	static void InstallFromZip(string zipPath, string installDir)
	{
		ReplaceInstallDir(installDir, () => ZipFile.ExtractToDirectory(zipPath, installDir));
	}

	static void InstallAppImage(string appImagePath, string installDir)
	{
		ReplaceInstallDir(installDir, () =>
		{
			// The AppImage is a single self-mounting executable - no extraction needed,
			// just move it into place and mark it executable.
			Directory.CreateDirectory(installDir);
			var target = GameInstall.GameExePath(installDir);
			File.Copy(appImagePath, target, overwrite: true);
			MakeExecutable(target);
		});
	}

	static void InstallFromDmg(string dmgPath, string installDir)
	{
		var mountPoint = Path.Combine(Path.GetTempPath(), $"cn-launcher-mount-{Guid.NewGuid():N}");
		Directory.CreateDirectory(mountPoint);

		try
		{
			RunProcess("hdiutil", ["attach", dmgPath, "-mountpoint", mountPoint, "-nobrowse", "-quiet"]);

			var appBundle = Directory.GetDirectories(mountPoint, "*.app").FirstOrDefault()
				?? throw new InvalidOperationException("The disk image does not contain an .app bundle.");

			ReplaceInstallDir(installDir, () =>
			{
				Directory.CreateDirectory(installDir);
				var destApp = Path.Combine(installDir, GameInstall.MacAppBundleName);
				CopyDirectory(appBundle, destApp);

				// Unsigned build - clear any quarantine flag so Gatekeeper does not block it,
				// and make sure the bundle executables kept their execute bit through the copy.
				RunProcess("xattr", ["-cr", destApp]);
				foreach (var f in Directory.GetFiles(Path.Combine(destApp, "Contents", "MacOS"), "*", SearchOption.AllDirectories))
					MakeExecutable(f);
			});
		}
		finally
		{
			TryRunProcess("hdiutil", ["detach", mountPoint, "-quiet"]);
			TryDeleteDirectory(mountPoint);
		}
	}

	/// <summary>
	/// Replaces the install directory wholesale, which is the only reliable way to drop
	/// files a newer build removed. A "Support" folder is carried across because the engine
	/// treats it as a portable settings/replays/content store (see GameInstall.SupportDir).
	/// </summary>
	static void ReplaceInstallDir(string installDir, Action populate)
	{
		var supportDir = Path.Combine(installDir, "Support");
		string? preservedSupport = null;

		if (Directory.Exists(supportDir))
		{
			preservedSupport = Path.Combine(Path.GetDirectoryName(installDir.TrimEnd(Path.DirectorySeparatorChar))
				?? Path.GetTempPath(), $"cn-support-{Guid.NewGuid():N}");
			Directory.Move(supportDir, preservedSupport);
		}

		try
		{
			if (Directory.Exists(installDir))
				Directory.Delete(installDir, recursive: true);

			populate();
		}
		finally
		{
			if (preservedSupport != null && Directory.Exists(preservedSupport))
			{
				Directory.CreateDirectory(installDir);
				Directory.Move(preservedSupport, supportDir);
			}
		}
	}

	static void MakeExecutable(string path)
	{
		if (OperatingSystem.IsWindows())
			return;

		File.SetUnixFileMode(path,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
			UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
	}

	static void CopyDirectory(string sourceDir, string destDir)
	{
		Directory.CreateDirectory(destDir);

		foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
			Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));

		foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
			File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
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

	static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception)
		{
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
