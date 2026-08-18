using System.Diagnostics;

namespace CNLauncher.Services;

/// <summary>Opening links and folders in whatever the host desktop uses.</summary>
public static class Shell
{
	public const string DiscordInvite = "https://discord.gg/pnfWaubrRw";

	public static void OpenUrl(string url)
	{
		try
		{
			// UseShellExecute is what hands the URL to the default browser; on Linux the
			// .NET implementation shells out to xdg-open for us.
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception)
		{
			// No browser configured - not worth interrupting the user over.
		}
	}

	public static void OpenFolder(string path)
	{
		try
		{
			if (!Directory.Exists(path))
				return;

			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch (Exception)
		{
		}
	}
}
