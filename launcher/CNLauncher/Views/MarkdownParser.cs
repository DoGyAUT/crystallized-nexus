using System.Text.RegularExpressions;

namespace CNLauncher.Views;

/// <summary>A styled run of text produced by <see cref="MarkdownParser"/>.</summary>
public readonly record struct MarkdownSegment(
	string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Heading = false)
{
	/// <summary>A line break carries no text; the renderer turns it into a LineBreak inline.</summary>
	public static readonly MarkdownSegment Break = new("\n");

	public bool IsBreak => Text == "\n" && !Bold && !Italic && !Code && !Heading;
}

/// <summary>
/// Parses the small slice of markdown that actually turns up in GitHub release notes -
/// bold, italic, inline code, headings and bullet lists.
///
/// Deliberately not a markdown library: the launcher ships fully trimmed and every
/// dependency costs binary size, while release notes never use tables, images or nested
/// block structures. Anything not understood is emitted as plain text rather than dropped,
/// so an unsupported construct degrades to something readable instead of vanishing.
///
/// Kept free of any Avalonia reference so the rules below can be tested directly.
/// </summary>
public static class MarkdownParser
{
	// Bold is matched before italic so "**x**" is not read as an italic "*" wrapping "*x*".
	//
	// Two guards keep ordinary prose intact. The delimited text may neither begin nor end
	// with whitespace, which is what stops "damage is 2 * 3 * 4" from turning into italics;
	// and the italic forms additionally require a non-word boundary outside, which is what
	// leaves snake_case identifiers and paths like "rules/ai_bot.yaml" alone.
	const string NoOuterSpace = @"[^\s{0}](?:[^{0}\n]*[^\s{0}])?";

	static readonly Regex InlinePattern = new(
		@"(?<bold>\*\*(?<boldText>" + Delimited('*') + @")\*\*" +
		@"|__(?<boldText2>" + Delimited('_') + @")__)" +
		@"|(?<code>`(?<codeText>[^`]+?)`)" +
		@"|(?<italic>(?<![\w*])\*(?<italicText>" + Delimited('*') + @")\*(?![\w*])" +
		@"|(?<![\w_])_(?<italicText2>" + Delimited('_') + @")_(?![\w_]))",
		RegexOptions.Compiled | RegexOptions.ExplicitCapture);

	static string Delimited(char marker)
		=> string.Format(System.Globalization.CultureInfo.InvariantCulture,
			NoOuterSpace, Regex.Escape(marker.ToString()));

	static readonly Regex HeadingPattern = new(@"^(?<level>#{1,6})\s+(?<text>.*)$", RegexOptions.Compiled);
	static readonly Regex BulletPattern = new(@"^\s*[-*+]\s+(?<text>.*)$", RegexOptions.Compiled);

	public static IReadOnlyList<MarkdownSegment> Parse(string? markdown)
	{
		var segments = new List<MarkdownSegment>();
		if (string.IsNullOrEmpty(markdown))
			return segments;

		var lines = markdown.Replace("\r\n", "\n").Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			if (i > 0)
				segments.Add(MarkdownSegment.Break);

			var line = lines[i];

			var heading = HeadingPattern.Match(line);
			if (heading.Success)
			{
				AddInline(segments, heading.Groups["text"].Value, forceBold: true, heading: true);
				continue;
			}

			var bullet = BulletPattern.Match(line);
			if (bullet.Success)
			{
				segments.Add(new MarkdownSegment("  •  "));
				AddInline(segments, bullet.Groups["text"].Value);
				continue;
			}

			AddInline(segments, line);
		}

		return segments;
	}

	static void AddInline(List<MarkdownSegment> segments, string text, bool forceBold = false, bool heading = false)
	{
		void Plain(string value)
		{
			if (value.Length > 0)
				segments.Add(new MarkdownSegment(value, Bold: forceBold, Heading: heading));
		}

		var position = 0;
		foreach (Match match in InlinePattern.Matches(text))
		{
			Plain(text[position..match.Index]);

			if (match.Groups["bold"].Success)
			{
				segments.Add(new MarkdownSegment(
					match.Groups["boldText"].Success ? match.Groups["boldText"].Value : match.Groups["boldText2"].Value,
					Bold: true, Heading: heading));
			}
			else if (match.Groups["code"].Success)
			{
				segments.Add(new MarkdownSegment(
					match.Groups["codeText"].Value, Bold: forceBold, Code: true, Heading: heading));
			}
			else
			{
				segments.Add(new MarkdownSegment(
					match.Groups["italicText"].Success ? match.Groups["italicText"].Value : match.Groups["italicText2"].Value,
					Bold: forceBold, Italic: true, Heading: heading));
			}

			position = match.Index + match.Length;
		}

		Plain(text[position..]);
	}
}
