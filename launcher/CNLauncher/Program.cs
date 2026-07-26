using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CNLauncher;

static class Program
{
	const string RepoOwner = "DoGyAUT";
	const string RepoName = "crystallized-nexus";

	// Windows ships a "winportable" zip (flat folder, CrystallizedNexus.exe at the root).
	// Linux ships a single self-mounting AppImage - no extraction, just chmod +x and run.
	// The AppImage's AppRun script execs "usr/bin/openra-cn" internally, so that's the
	// process name to watch for, not the AppImage filename itself.
	// macOS ships a .dmg disk image containing a "OpenRA - <mod name>.app" bundle; we mount
	// it, copy the .app out under our own fixed name, and detach. The bundle's
	// CFBundleExecutable is "Launcher" (a native stub that NSTask-spawns the actual game and
	// stays running for the whole session), so that's both what we exec and what we watch
	// for when checking if the game is still running.
	const string WindowsGameExeName = "CrystallizedNexus.exe";
	const string LinuxGameFileName = "CrystallizedNexus.AppImage";
	const string LinuxGameProcessName = "openra-cn";
	const string MacAppBundleName = "CrystallizedNexus.app";
	const string MacGameProcessName = "Launcher";

	static readonly string LauncherDir = AppContext.BaseDirectory;
	static readonly string GameDir = Path.Combine(LauncherDir, "game");
	static readonly string VersionFile = Path.Combine(LauncherDir, "version.txt");

	static readonly string GameExe =
		OperatingSystem.IsWindows() ? Path.Combine(GameDir, WindowsGameExeName) :
		OperatingSystem.IsMacOS() ? Path.Combine(GameDir, MacAppBundleName, "Contents", "MacOS", MacGameProcessName) :
		Path.Combine(GameDir, LinuxGameFileName);

	static async Task<int> Main(string[] args)
	{
		Console.WriteLine("Crystallized Nexus test launcher");

		if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
		{
			Console.WriteLine("Unsupported platform (only Windows, Linux and macOS builds are published).");
			Pause();
			return 1;
		}

		using var http = new HttpClient();
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CNLauncher", "1.0"));
		http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		if (!string.IsNullOrEmpty(Secrets.GitHubToken))
			http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secrets.GitHubToken);

		Release? latest;
		try
		{
			latest = await GetLatestRelease(http);
		}
		catch (Exception e)
		{
			Console.WriteLine($"Could not check for updates ({e.Message}).");
			latest = null;
		}

		var installedVersion = File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null;

		if (latest == null && installedVersion == null)
		{
			Console.WriteLine("No local install and update check failed. Check your internet connection and try again.");
			Pause();
			return 1;
		}

		if (latest != null && latest.TagName != installedVersion)
		{
			Console.WriteLine($"Update available: {installedVersion ?? "(none installed)"} -> {latest.TagName}");

			if (!WaitForGameToClose())
				return 1;

			try
			{
				await DownloadAndInstall(http, latest);
				File.WriteAllText(VersionFile, latest.TagName);
				Console.WriteLine($"Installed {latest.TagName}.");
			}
			catch (Exception e)
			{
				Console.WriteLine($"Update failed: {e.Message}");
				if (installedVersion == null)
				{
					Pause();
					return 1;
				}

				Console.WriteLine("Launching the previously installed version instead.");
			}
		}
		else
		{
			Console.WriteLine($"Up to date ({installedVersion}).");
		}

		return LaunchGame();
	}

	static bool WaitForGameToClose()
	{
		bool IsRunning() => OperatingSystem.IsWindows()
			? Process.GetProcessesByName("OpenRA").Length > 0 || Process.GetProcessesByName("CrystallizedNexus").Length > 0
			: Process.GetProcessesByName(OperatingSystem.IsMacOS() ? MacGameProcessName : LinuxGameProcessName).Length > 0;

		while (IsRunning())
		{
			Console.WriteLine("Please close the running game before updating, then press Enter.");
			Console.ReadLine();
		}

		return true;
	}

	sealed record Release(string TagName, ReleaseAsset[] Assets);
	sealed record ReleaseAsset(long Id, string Name);

	static async Task<Release?> GetLatestRelease(HttpClient http)
	{
		var response = await http.GetAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases");
		response.EnsureSuccessStatusCode();

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
		var releases = doc.RootElement;
		if (releases.GetArrayLength() == 0)
			return null;

		var first = releases[0];
		var tagName = first.GetProperty("tag_name").GetString()!;
		var assets = first.GetProperty("assets")
			.EnumerateArray()
			.Select(a => new ReleaseAsset(a.GetProperty("id").GetInt64(), a.GetProperty("name").GetString()!))
			.ToArray();

		return new Release(tagName, assets);
	}

	static ReleaseAsset PickAsset(Release release)
	{
		// The packaging workflow attaches Windows (x86 + x64), Linux and macOS assets to the
		// same release.
		if (OperatingSystem.IsWindows())
		{
			return release.Assets.FirstOrDefault(a =>
					a.Name.Contains("winportable", StringComparison.OrdinalIgnoreCase) &&
					a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException($"Release {release.TagName} has no Windows x64 winportable asset.");
		}

		if (OperatingSystem.IsMacOS())
		{
			return release.Assets.FirstOrDefault(a => a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException($"Release {release.TagName} has no macOS disk image asset.");
		}

		return release.Assets.FirstOrDefault(a => a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException($"Release {release.TagName} has no Linux AppImage asset.");
	}

	static async Task DownloadAndInstall(HttpClient http, Release release)
	{
		var asset = PickAsset(release);
		Console.WriteLine($"Downloading {asset.Name}...");

		// Private-repo release assets must be fetched via the API asset endpoint with an
		// octet-stream Accept header - the plain browser_download_url needs a signed
		// session and won't work with a bare token.
		var request = new HttpRequestMessage(HttpMethod.Get,
			$"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/assets/{asset.Id}");
		request.Headers.Accept.Clear();
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

		using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		response.EnsureSuccessStatusCode();
		var totalBytes = response.Content.Headers.ContentLength;

		if (OperatingSystem.IsWindows())
		{
			var zipPath = Path.Combine(Path.GetTempPath(), $"cn-launcher-{Guid.NewGuid():N}.zip");
			await using (var fileStream = File.Create(zipPath))
			await using (var responseStream = await response.Content.ReadAsStreamAsync())
				await CopyWithProgressAsync(responseStream, fileStream, totalBytes);

			Console.WriteLine("Extracting...");
			try
			{
				if (Directory.Exists(GameDir))
					Directory.Delete(GameDir, recursive: true);

				ZipFile.ExtractToDirectory(zipPath, GameDir);
			}
			finally
			{
				File.Delete(zipPath);
			}
		}
		else if (OperatingSystem.IsMacOS())
		{
			var dmgPath = Path.Combine(Path.GetTempPath(), $"cn-launcher-{Guid.NewGuid():N}.dmg");
			await using (var fileStream = File.Create(dmgPath))
			await using (var responseStream = await response.Content.ReadAsStreamAsync())
				await CopyWithProgressAsync(responseStream, fileStream, totalBytes);

			var mountPoint = Path.Combine(Path.GetTempPath(), $"cn-launcher-mount-{Guid.NewGuid():N}");
			Directory.CreateDirectory(mountPoint);

			Console.WriteLine("Mounting disk image...");
			try
			{
				RunProcess("hdiutil", ["attach", dmgPath, "-mountpoint", mountPoint, "-nobrowse", "-quiet"]);

				var appBundle = Directory.GetDirectories(mountPoint, "*.app").FirstOrDefault()
					?? throw new InvalidOperationException("Disk image does not contain an .app bundle.");

				var destApp = Path.Combine(GameDir, MacAppBundleName);
				if (Directory.Exists(GameDir))
					Directory.Delete(GameDir, recursive: true);

				Directory.CreateDirectory(GameDir);
				Console.WriteLine("Copying application...");
				CopyDirectory(appBundle, destApp);

				RunProcess("hdiutil", ["detach", mountPoint, "-quiet"]);

				// Unsigned build - clear any quarantine flag so Gatekeeper doesn't block it,
				// and make sure the bundle executables kept their execute bit through the copy.
				RunProcess("xattr", ["-cr", destApp]);
				foreach (var f in Directory.GetFiles(Path.Combine(destApp, "Contents", "MacOS"), "*", SearchOption.AllDirectories))
					File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
						UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			}
			finally
			{
				Directory.Delete(mountPoint, recursive: true);
				File.Delete(dmgPath);
			}
		}
		else
		{
			// The AppImage is a single self-mounting executable - no extraction needed,
			// just write it out and mark it executable.
			Directory.CreateDirectory(GameDir);
			await using (var fileStream = File.Create(GameExe))
			await using (var responseStream = await response.Content.ReadAsStreamAsync())
				await CopyWithProgressAsync(responseStream, fileStream, totalBytes);

			File.SetUnixFileMode(GameExe,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
				UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
				UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		}
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

	static async Task CopyWithProgressAsync(Stream source, Stream destination, long? totalBytes)
	{
		var buffer = new byte[81920];
		long bytesRead = 0;
		int read;
		while ((read = await source.ReadAsync(buffer)) > 0)
		{
			await destination.WriteAsync(buffer.AsMemory(0, read));
			bytesRead += read;
			ReportProgress(bytesRead, totalBytes);
		}

		Console.WriteLine();
	}

	static void ReportProgress(long bytesRead, long? totalBytes)
	{
		const int BarWidth = 30;
		var mb = bytesRead / 1024.0 / 1024.0;

		if (totalBytes is > 0)
		{
			var fraction = Math.Clamp((double)bytesRead / totalBytes.Value, 0, 1);
			var filled = (int)(fraction * BarWidth);
			var bar = new string('#', filled) + new string('-', BarWidth - filled);
			var totalMb = totalBytes.Value / 1024.0 / 1024.0;
			Console.Write($"\r[{bar}] {fraction * 100,5:0.0}%  {mb,7:0.0} / {totalMb:0.0} MB");
		}
		else
		{
			Console.Write($"\rDownloaded {mb,7:0.0} MB");
		}
	}

	static int LaunchGame()
	{
		if (!File.Exists(GameExe))
		{
			Console.WriteLine($"Could not find {GameExe}.");
			Pause();
			return 1;
		}

		// The packaged launcher (Windows exe / Linux AppRun / macOS .app) already has the
		// mod/engine/search-path arguments baked in - no extra arguments needed here.
		var psi = new ProcessStartInfo(GameExe)
		{
			WorkingDirectory = GameDir,
			UseShellExecute = false,
		};

		Console.WriteLine("Starting game...");
		using var process = Process.Start(psi);
		process?.WaitForExit();
		return process?.ExitCode ?? 1;
	}

	static void Pause()
	{
		Console.WriteLine("Press Enter to exit.");
		Console.ReadLine();
	}
}
