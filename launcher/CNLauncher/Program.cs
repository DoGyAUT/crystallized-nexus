using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CNLauncher;

static class Program
{
	const string RepoOwner = "DoGyAUT";
	const string RepoName = "crystallized-nexus";
	const string ModId = "cn";

	static readonly string LauncherDir = AppContext.BaseDirectory;
	static readonly string GameDir = Path.Combine(LauncherDir, "game");
	static readonly string VersionFile = Path.Combine(LauncherDir, "version.txt");
	static readonly string EngineExe = Path.Combine(GameDir, "engine", "bin", "OpenRA.exe");

	static async Task<int> Main(string[] args)
	{
		Console.WriteLine("Crystallized Nexus test launcher");

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
		while (Process.GetProcessesByName("OpenRA").Length > 0)
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

	static async Task DownloadAndInstall(HttpClient http, Release release)
	{
		var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException($"Release {release.TagName} has no .zip asset.");

		Console.WriteLine($"Downloading {asset.Name}...");

		// Private-repo release assets must be fetched via the API asset endpoint with an
		// octet-stream Accept header - the plain browser_download_url needs a signed
		// session and won't work with a bare token.
		var request = new HttpRequestMessage(HttpMethod.Get,
			$"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/assets/{asset.Id}");
		request.Headers.Accept.Clear();
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

		using var response = await http.SendAsync(request);
		response.EnsureSuccessStatusCode();

		var zipPath = Path.Combine(Path.GetTempPath(), $"cn-launcher-{Guid.NewGuid():N}.zip");
		await using (var fileStream = File.Create(zipPath))
			await response.Content.CopyToAsync(fileStream);

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

	static int LaunchGame()
	{
		if (!File.Exists(EngineExe))
		{
			Console.WriteLine($"Could not find {EngineExe}.");
			Pause();
			return 1;
		}

		var modsPath = Path.Combine(GameDir, "mods");
		var psi = new ProcessStartInfo(EngineExe)
		{
			WorkingDirectory = Path.GetDirectoryName(EngineExe),
			UseShellExecute = false,
		};

		psi.ArgumentList.Add($"Game.Mod={ModId}");
		psi.ArgumentList.Add("Engine.EngineDir=..");
		psi.ArgumentList.Add($"Engine.ModSearchPaths={modsPath},./mods");

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
