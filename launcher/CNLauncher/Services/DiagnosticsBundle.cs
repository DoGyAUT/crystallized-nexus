using System.IO.Compression;

namespace CNLauncher.Services;

/// <summary>
/// Packs the game's logs into a single zip the tester can drop into the Discord bug
/// channel, which is far more reliable than asking people to find the folder themselves.
/// </summary>
public static class DiagnosticsBundle
{
	public static string Create(string installDir)
	{
		var logsDir = GameInstall.LogsDir(installDir);
		if (!Directory.Exists(logsDir))
			throw new DirectoryNotFoundException($"No logs found at {logsDir}.");

		var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		var outputDir = Directory.Exists(desktop) ? desktop : LauncherConfig.ConfigDir;
		var zipPath = Path.Combine(outputDir, $"CrystallizedNexus-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

		using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
		{
			// The game holds its current log files open, so entries are added from a
			// read-share stream instead of via CreateEntryFromFile.
			foreach (var file in Directory.GetFiles(logsDir, "*", SearchOption.AllDirectories))
			{
				try
				{
					var entry = archive.CreateEntry(Path.GetRelativePath(logsDir, file), CompressionLevel.Optimal);
					using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					using var target = entry.Open();
					source.CopyTo(target);
				}
				catch (IOException)
				{
					// Skip anything genuinely unreadable rather than failing the whole bundle.
				}
			}

			var summary = archive.CreateEntry("launcher-info.txt");
			using var writer = new StreamWriter(summary.Open());
			writer.WriteLine($"Launcher version: {AppVersion.Current}");
			writer.WriteLine($"Installed game:   {GameInstall.ReadInstalledVersion(installDir) ?? "(none)"}");
			writer.WriteLine($"Install dir:      {installDir}");
			writer.WriteLine($"Support dir:      {GameInstall.SupportDir(installDir)}");
			writer.WriteLine($"TS content:       {(GameInstall.HasTiberianSunContent(installDir) ? "present" : "missing")}");
			writer.WriteLine($"OS:               {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
		}

		return zipPath;
	}
}
