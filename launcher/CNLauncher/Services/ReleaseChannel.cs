namespace CNLauncher.Services;

public enum ReleaseChannel
{
	/// <summary>Only finished builds ("release-*" and the older "v*" tags).</summary>
	Stable,

	/// <summary>Everything the stable channel has, plus "playtest-*" builds.</summary>
	Playtest,
}

public static class ReleaseChannelExtensions
{
	public static string DisplayName(this ReleaseChannel channel)
		=> channel == ReleaseChannel.Stable ? "Stable" : "Playtest (latest builds)";

	static bool IsStableGameTag(string tag)
		=> tag.StartsWith("release-", StringComparison.OrdinalIgnoreCase)
			|| (tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1]));

	static bool IsPlaytestGameTag(string tag)
		=> tag.StartsWith("playtest-", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True if this release is a game build the channel should offer. Launcher releases
	/// ("launcher-v*") live in the same repository and are never game builds, so anything
	/// that is not an explicitly recognised game tag is excluded rather than assumed.
	/// </summary>
	public static bool Accepts(this ReleaseChannel channel, Release release)
	{
		var tag = release.TagName;
		if (IsStableGameTag(tag))
			return true;

		return channel == ReleaseChannel.Playtest && IsPlaytestGameTag(tag);
	}
}
