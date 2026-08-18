using System.Reflection;

namespace CNLauncher.Services;

/// <summary>The launcher's own version, used for the UI and for the self-update check.</summary>
public static class AppVersion
{
	/// <summary>Normalised "major.minor.patch" of this build, e.g. "2.0.0".</summary>
	public static string Current { get; } = ReadCurrent();

	static string ReadCurrent()
	{
		var version = Assembly.GetExecutingAssembly().GetName().Version;
		return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
	}

	/// <summary>
	/// Parses a "launcher-v1.2.3" tag into a comparable version. Returns null for tags
	/// that are not launcher releases, which is how game releases get filtered out.
	/// </summary>
	public static Version? ParseLauncherTag(string tagName)
	{
		const string Prefix = "launcher-v";
		if (!tagName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
			return null;

		return Version.TryParse(tagName[Prefix.Length..], out var version) ? Normalise(version) : null;
	}

	public static Version Parse(string version)
		=> Version.TryParse(version, out var parsed) ? Normalise(parsed) : new Version(0, 0, 0);

	// Version treats unspecified components as -1, which makes "1.2" sort below "1.2.0".
	// Pinning them to zero keeps comparisons between differently written tags sane.
	static Version Normalise(Version version)
		=> new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
