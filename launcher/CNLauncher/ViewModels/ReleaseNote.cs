using System.Globalization;
using CNLauncher.Services;

namespace CNLauncher.ViewModels;

/// <summary>One build's entry in the "What's new" history.</summary>
public sealed record ReleaseNote(string Tag, string Meta, string Body, bool IsCurrent, bool IsUnread);

public static class ReleaseNoteBuilder
{
	/// <summary>
	/// Builds the newest-first history shown in the launcher. Every build in the selected
	/// channel is listed, not only the installable one - a tester who skipped three
	/// playtests should be able to read what happened in all of them, which is the whole
	/// point of keeping the panel scrollable.
	/// </summary>
	public static IReadOnlyList<ReleaseNote> Build(
		IReadOnlyList<Release> releases, ReleaseChannel channel, string? installedTag)
	{
		var history = releases.Where(r => channel.Accepts(r)).ToList();

		// Everything published after the installed build is what the player has not seen.
		// Position in the list, not dates: the list is already GitHub's newest-first order.
		var installedIndex = installedTag == null
			? -1
			: history.FindIndex(r => r.TagName == installedTag);

		return history.Select((release, index) => new ReleaseNote(
			release.TagName,
			BuildMeta(release, index, installedIndex),
			Clean(release.Notes),
			IsCurrent: index == installedIndex,
			IsUnread: installedIndex < 0 || index < installedIndex)).ToList();
	}

	static string BuildMeta(Release release, int index, int installedIndex)
	{
		var date = release.PublishedAt == DateTimeOffset.MinValue
			? ""
			: release.PublishedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

		if (index == installedIndex)
			return date.Length > 0 ? $"{date}  ·  you have this build" : "you have this build";

		return date;
	}

	static string Clean(string body)
	{
		if (string.IsNullOrWhiteSpace(body))
			return "No release notes for this build.";

		// Markdown is kept intact here - MarkdownText in the view turns headings, bold,
		// italic, code and bullets into styled inlines.
		var lines = body.Replace("\r\n", "\n").Split('\n')
			.Select(l => l.TrimEnd())

			// The compare link that --generate-notes appends is dead weight in a launcher,
			// where it is not clickable anyway.
			.Where(l => !l.StartsWith("**Full Changelog**", StringComparison.Ordinal));

		return string.Join('\n', lines).Trim();
	}
}
