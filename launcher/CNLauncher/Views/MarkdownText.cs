using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace CNLauncher.Views;

/// <summary>
/// Attached property that renders release-note markdown into a TextBlock's inlines.
/// The parsing rules live in <see cref="MarkdownParser"/>; this is only the bridge to
/// Avalonia's text model.
/// </summary>
public static class MarkdownText
{
	static readonly FontFamily MonospaceFont = FontFamily.Parse("Consolas, Menlo, DejaVu Sans Mono, monospace");

	public static readonly AttachedProperty<string?> TextProperty =
		AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(MarkdownText));

	static MarkdownText()
	{
		TextProperty.Changed.AddClassHandler<TextBlock>((target, e) => Render(target, e.NewValue as string));
	}

	public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

	public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

	static void Render(TextBlock target, string? markdown)
	{
		target.Inlines?.Clear();

		var segments = MarkdownParser.Parse(markdown);
		if (segments.Count == 0)
			return;

		var inlines = target.Inlines ??= [];
		foreach (var segment in segments)
		{
			if (segment.IsBreak)
			{
				inlines.Add(new LineBreak());
				continue;
			}

			var run = new Run(segment.Text);
			if (segment.Bold)
				run.FontWeight = FontWeight.Bold;

			if (segment.Italic)
				run.FontStyle = FontStyle.Italic;

			if (segment.Code)
				run.FontFamily = MonospaceFont;

			if (segment.Heading)
				run.FontSize = 14;

			inlines.Add(run);
		}
	}
}
