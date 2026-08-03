using System.Net;
using System.Text;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class SafeMarkdownRenderer
{
    public static RenderedMarkdownContent Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var links = new List<MarkdownExternalLink>();
        var html = new StringBuilder(Math.Min(markdown.Length * 2, LibraryRules.MaximumRenderedMarkdownCharacters * 3));
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var inFence = false;
        var fence = new StringBuilder();
        string? listTag = null;

        void CloseList()
        {
            if (listTag is null) return;
            html.Append("</").Append(listTag).Append('>');
            listTag = null;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                CloseList();
                if (inFence)
                {
                    html.Append("<pre><code>").Append(WebUtility.HtmlEncode(fence.ToString().TrimEnd('\n'))).Append("</code></pre>");
                    fence.Clear();
                    inFence = false;
                }
                else
                {
                    inFence = true;
                }
                continue;
            }
            if (inFence)
            {
                fence.Append(line).Append('\n');
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                CloseList();
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed is "---" or "***" or "___")
            {
                CloseList();
                html.Append("<hr />");
                continue;
            }
            var headingLevel = HeadingLevel(line);
            if (headingLevel > 0)
            {
                CloseList();
                html.Append("<h").Append(headingLevel).Append('>');
                AppendInline(html, line[(headingLevel + 1)..].Trim(), links, 0);
                html.Append("</h").Append(headingLevel).Append('>');
                continue;
            }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                CloseList();
                html.Append("<blockquote>");
                AppendInline(html, trimmed[2..], links, 0);
                html.Append("</blockquote>");
                continue;
            }
            if (TryListItem(trimmed, out var ordered, out var itemText))
            {
                var requestedTag = ordered ? "ol" : "ul";
                if (!string.Equals(listTag, requestedTag, StringComparison.Ordinal))
                {
                    CloseList();
                    listTag = requestedTag;
                    html.Append('<').Append(listTag).Append('>');
                }
                html.Append("<li>");
                AppendInline(html, itemText, links, 0);
                html.Append("</li>");
                continue;
            }

            CloseList();
            html.Append("<p>");
            AppendInline(html, trimmed, links, 0);
            while (index + 1 < lines.Length && IsParagraphContinuation(lines[index + 1]))
            {
                html.Append(' ');
                index++;
                AppendInline(html, lines[index].Trim(), links, 0);
            }
            html.Append("</p>");
        }

        CloseList();
        if (inFence)
        {
            html.Append("<pre><code>").Append(WebUtility.HtmlEncode(fence.ToString().TrimEnd('\n'))).Append("</code></pre>");
        }
        return new RenderedMarkdownContent(html.ToString(), links);
    }

    private static void AppendInline(StringBuilder html, string text, List<MarkdownExternalLink> links, int depth)
    {
        if (depth > 8)
        {
            html.Append(WebUtility.HtmlEncode(text));
            return;
        }
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '`' && TryDelimited(text, index, "`", out var codeEnd))
            {
                html.Append("<code>").Append(WebUtility.HtmlEncode(text[(index + 1)..codeEnd])).Append("</code>");
                index = codeEnd + 1;
                continue;
            }
            if (text.AsSpan(index).StartsWith("**", StringComparison.Ordinal) && TryDelimited(text, index, "**", out var strongEnd))
            {
                html.Append("<strong>");
                AppendInline(html, text[(index + 2)..strongEnd], links, depth + 1);
                html.Append("</strong>");
                index = strongEnd + 2;
                continue;
            }
            if (text[index] == '*' && TryDelimited(text, index, "*", out var emphasisEnd))
            {
                html.Append("<em>");
                AppendInline(html, text[(index + 1)..emphasisEnd], links, depth + 1);
                html.Append("</em>");
                index = emphasisEnd + 1;
                continue;
            }
            if ((text[index] == '[' || (text[index] == '!' && index + 1 < text.Length && text[index + 1] == '['))
                && TryLink(text, index, out var consumed, out var label, out var destination, out var image))
            {
                if (image)
                {
                    html.Append("<span class=\"markdown-image-reference\">Image: ").Append(WebUtility.HtmlEncode(label));
                    if (destination.Length > 0) html.Append(" (").Append(WebUtility.HtmlEncode(destination)).Append(')');
                    html.Append("</span>");
                }
                else
                {
                    html.Append("<span class=\"markdown-link\">");
                    AppendInline(html, label, links, depth + 1);
                    html.Append(" <span class=\"markdown-destination\">(").Append(WebUtility.HtmlEncode(destination)).Append(")</span></span>");
                    if (IsAllowedExternalDestination(destination)) links.Add(new MarkdownExternalLink(label, destination));
                }
                index += consumed;
                continue;
            }
            var next = index + 1;
            while (next < text.Length && text[next] is not '`' and not '*' and not '[' and not '!') next++;
            html.Append(WebUtility.HtmlEncode(text[index..next]));
            index = next;
        }
    }

    private static bool TryDelimited(string text, int start, string delimiter, out int end)
    {
        end = text.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
        return end > start + delimiter.Length;
    }

    private static bool TryLink(string text, int start, out int consumed, out string label, out string destination, out bool image)
    {
        image = text[start] == '!';
        var labelStart = start + (image ? 2 : 1);
        var labelEnd = text.IndexOf(']', labelStart);
        if (labelEnd < labelStart || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
        {
            consumed = 0;
            label = destination = string.Empty;
            return false;
        }
        var destinationEnd = text.IndexOf(')', labelEnd + 2);
        if (destinationEnd < 0)
        {
            consumed = 0;
            label = destination = string.Empty;
            return false;
        }
        label = text[labelStart..labelEnd];
        destination = text[(labelEnd + 2)..destinationEnd].Trim();
        consumed = destinationEnd - start + 1;
        return true;
    }

    private static bool IsAllowedExternalDestination(string destination) =>
        Uri.TryCreate(destination, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" or "mailto";

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && level < 6 && line[level] == '#') level++;
        return level > 0 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static bool TryListItem(string trimmed, out bool ordered, out string text)
    {
        if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            ordered = false;
            text = trimmed[2..];
            return true;
        }
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
        {
            ordered = true;
            text = trimmed[(digits + 2)..];
            return true;
        }
        ordered = false;
        text = string.Empty;
        return false;
    }

    private static bool IsParagraphContinuation(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.Trim();
        return HeadingLevel(line) == 0
            && !trimmed.StartsWith("```", StringComparison.Ordinal)
            && !trimmed.StartsWith("> ", StringComparison.Ordinal)
            && trimmed is not "---" and not "***" and not "___"
            && !TryListItem(trimmed, out _, out _);
    }
}
