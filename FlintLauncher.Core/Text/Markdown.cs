// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/Markdown.{h,cpp}. The C++ module delegates to cmark
// (cmark_markdown_to_html with CMARK_OPT_NOBREAKS | CMARK_OPT_UNSAFE). Since cmark is not
// available as a native dependency here, this is a hand-rolled converter for the CommonMark
// subset PrismLauncher actually renders (chat/announcement markdown):
//   headings, paragraphs, fenced code blocks, blockquotes, bullet/ordered lists,
//   thematic breaks, HTML blocks (passed through: UNSAFE), and inline code spans,
//   autolinks, raw inline HTML (UNSAFE), links, images, emphasis/strong, entities,
//   hard breaks. NOBREAKS renders soft line breaks as spaces.
// Known deviations from cmark: no reference links, no strikethrough/tables (extensions),
// simplified emphasis delimiter matching (same-char runs must balance one nesting level),
// simplified list indentation handling, and HTML blocks end at the first blank line.

using System.Text;
using System.Text.RegularExpressions;

namespace FlintLauncher.Core.Text;

public static partial class Markdown
{
    [GeneratedRegex(@"^[ ]{0,3}(?:-{3,}|\*{3,}|_{3,})[ \t]*$")]
    private static partial Regex ThematicBreakRegex();

    [GeneratedRegex(@"^(#{1,6})[ \t]+(?<text>.*?)#*[ \t]*$")]
    private static partial Regex AtxHeadingRegex();

    [GeneratedRegex(@"^[ ]{0,3}```(?<info>\S*)[ \t]*$|^[ ]{0,3}~~~(?<info>\S*)[ \t]*$")]
    private static partial Regex FenceOpenRegex();

    [GeneratedRegex(@"^(?:<([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^>]*)?/?>\s*)+$")]
    private static partial Regex HtmlLineRegex();

    /// <summary>cmark_markdown_to_html equivalent (NOBREAKS | UNSAFE).</summary>
    public static string MarkdownToHtml(string markdown)
    {
        var blocks = RenderBlocks(markdown.Replace("\r\n", "\n").Replace("\r", "\n"));
        return blocks.ToString().TrimEnd('\n');
    }

    // ------------------------------------------------------------- block level

    private static StringBuilder RenderBlocks(string source)
    {
        var lines = source.Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }

            // fenced code block
            var fence = FenceOpenRegex().Match(line);
            if (fence.Success)
            {
                var marker = line.TrimStart()[0] == '~' ? '~' : '`';
                i++;
                html.Append("<pre><code");
                var info = fence.Groups["info"].Value;
                if (info.Length != 0)
                {
                    html.Append(" class=\"language-").Append(Escape(info)).Append('"');
                }
                html.Append('>');
                while (i < lines.Length && !IsFenceClose(lines[i], marker))
                {
                    html.Append(Escape(lines[i])).Append('\n');
                    i++;
                }
                i++; // closing fence (or EOF)
                html.Append("</code></pre>\n");
                continue;
            }

            // ATX heading
            var heading = AtxHeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                html.Append('<').Append(level).Append('>')
                    .Append(RenderInline(heading.Groups["text"].Value.Trim()))
                    .Append("</").Append(level).Append(">\n");
                i++;
                continue;
            }

            // thematic break
            if (ThematicBreakRegex().IsMatch(line))
            {
                html.Append("<hr />\n");
                i++;
                continue;
            }

            // blockquote
            if (line.TrimStart().StartsWith('>'))
            {
                var inner = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var stripped = lines[i].TrimStart()[1..];
                    inner.Add(stripped.StartsWith(' ') ? stripped[1..] : stripped);
                    i++;
                }
                html.Append("<blockquote>\n").Append(RenderBlocks(string.Join('\n', inner))).Append("</blockquote>\n");
                continue;
            }

            // list (bullet or ordered)
            var listMarker = MatchListMarker(line, out var ordered, out var contentIndent);
            if (listMarker >= 0)
            {
                var (listHtml, next) = RenderList(lines, i, ordered, contentIndent);
                html.Append(listHtml);
                i = next;
                continue;
            }

            // raw HTML block (CMARK_OPT_UNSAFE): pass through until a blank line
            if (HtmlLineRegex().IsMatch(line.Trim()))
            {
                while (i < lines.Length && lines[i].Trim().Length != 0)
                {
                    html.Append(lines[i]).Append('\n');
                    i++;
                }
                continue;
            }

            // paragraph: gather until blank line or a new block construct
            var paragraph = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length != 0)
            {
                var current = lines[i];
                if (paragraph.Count > 0 &&
                    (FenceOpenRegex().IsMatch(current) || AtxHeadingRegex().IsMatch(current) ||
                     ThematicBreakRegex().IsMatch(current) || current.TrimStart().StartsWith('>') ||
                     MatchListMarker(current, out _, out _) >= 0 || HtmlLineRegex().IsMatch(current.Trim())))
                {
                    break;
                }
                paragraph.Add(current);
                i++;
            }
            html.Append("<p>").Append(RenderParagraph(paragraph)).Append("</p>\n");
        }
        return html;
    }

    private static (string, int) RenderList(string[] lines, int start, bool ordered, int contentIndent)
    {
        var tag = ordered ? "ol" : "ul";
        var html = new StringBuilder("<").Append(tag).Append(">\n");
        var i = start;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                // blank line: loose list item separator; end the list when nothing follows
                var j = i + 1;
                if (j >= lines.Length || MatchListMarker(lines[j], out var nextOrdered, out _) < 0 || nextOrdered != ordered)
                {
                    break;
                }
                i = j;
                continue;
            }
            var markerPos = MatchListMarker(line, out var itemOrdered, out var indent);
            if (markerPos < 0 || itemOrdered != ordered)
            {
                break;
            }
            var firstContent = line.Length > indent ? line[indent..] : "";
            var itemLines = new List<string> { firstContent };
            i++;
            while (i < lines.Length && lines[i].Trim().Length != 0)
            {
                var cont = lines[i];
                var contIndent = cont.Length - cont.TrimStart().Length;
                if (MatchListMarker(cont, out var contOrdered, out _) >= 0 && contOrdered == ordered)
                {
                    break; // next item
                }
                if (contIndent >= contentIndent)
                {
                    itemLines.Add(cont.Length > contentIndent ? cont[contentIndent..] : cont.TrimStart());
                    i++;
                }
                else
                {
                    break;
                }
            }
            html.Append("<li>");
            if (itemLines.Count == 1)
            {
                html.Append(RenderInline(itemLines[0].Trim()));
            }
            else
            {
                html.Append(RenderBlocks(string.Join('\n', itemLines)));
            }
            html.Append("</li>\n");
        }
        html.Append("</").Append(tag).Append(">\n");
        return (html.ToString(), i);
    }

    /// <summary>Returns the index of the list marker on the line, or -1.</summary>
    private static int MatchListMarker(string line, out bool ordered, out int contentIndent)
    {
        ordered = false;
        contentIndent = 0;
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }
        if (indent > 3 || indent >= line.Length)
        {
            return -1;
        }
        var rest = line[indent..];
        if (rest.Length >= 2 && (rest[0] is '*' or '-' or '+') && (rest[1] == ' ' || rest[1] == '\t'))
        {
            ordered = false;
            contentIndent = indent + 2;
            return indent;
        }
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }
        if (digits > 0 && digits <= 9 && digits + 1 < rest.Length && rest[digits] == '.' &&
            (rest[digits + 1] == ' ' || rest[digits + 1] == '\t'))
        {
            ordered = true;
            contentIndent = indent + digits + 2;
            return indent;
        }
        return -1;
    }

    /// <summary>
    /// Render paragraph lines as one inline run. CMARK_OPT_NOBREAKS: soft line breaks
    /// become single spaces; hard breaks (two trailing spaces or a trailing backslash)
    /// become &lt;br /&gt;.
    /// </summary>
    private static string RenderParagraph(List<string> lines)
    {
        var html = new StringBuilder();
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            var isLast = l == lines.Count - 1;
            if (line.EndsWith('\\'))
            {
                html.Append(RenderInline(line[..^1])).Append("<br />\n");
                continue;
            }
            var stripped = line.TrimEnd();
            if (!isLast && stripped.Length != line.Length && line.Length - stripped.Length >= 2)
            {
                html.Append(RenderInline(stripped)).Append("<br />\n");
                continue;
            }
            html.Append(RenderInline(line));
            if (!isLast)
            {
                html.Append(' '); // soft break with NOBREAKS
            }
        }
        return html.ToString();
    }

    private static bool IsFenceClose(string line, char marker)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }
        foreach (var c in trimmed)
        {
            if (c != marker)
            {
                return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------- inline level

    private static string RenderInline(string text)
    {
        var html = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                html.Append(Escape(text[i + 1]));
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var run = RunLength(text, i, '`');
                var close = FindCodeSpanClose(text, i + run, run);
                if (close >= 0)
                {
                    var code = text[(i + run)..close];
                    code = code.Length > 1 && code.StartsWith(' ') && code.EndsWith(' ') && code.Trim().Length > 0
                        ? code[1..^1]
                        : code;
                    html.Append("<code>").Append(Escape(code)).Append("</code>");
                    i = close + run;
                    continue;
                }
                html.Append(Escape(new string('`', run)));
                i += run;
                continue;
            }
            if (c == '<')
            {
                // autolink <http://...> / <mailto or bare email>
                var close = text.IndexOf('>', i + 1);
                if (close > i)
                {
                    var inner = text[(i + 1)..close];
                    if (inner.Length > 0 && !inner.Contains(' ') && !inner.Contains('<') &&
                        Uri.TryCreate(inner, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto" || uri.Scheme == "ftp"))
                    {
                        html.Append("<a href=\"").Append(Attr(inner)).Append("\">").Append(Escape(inner)).Append("</a>");
                        i = close + 1;
                        continue;
                    }
                    // raw inline HTML: passed through (CMARK_OPT_UNSAFE)
                    if (IsValidHtmlTag(text, i, close))
                    {
                        html.Append(text[i..(close + 1)]);
                        i = close + 1;
                        continue;
                    }
                }
                html.Append("&lt;");
                i++;
                continue;
            }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                if (TryRenderLink(text, i + 1, html, image: true))
                {
                    i = AdvanceLink(text, i + 1);
                    continue;
                }
            }
            if (c == '[')
            {
                if (TryRenderLink(text, i, html, image: false))
                {
                    i = AdvanceLink(text, i);
                    continue;
                }
            }
            if (c is '*' or '_')
            {
                var run = RunLength(text, i, c);
                var close = FindEmphasisClose(text, i + run, c, run);
                if (close >= 0)
                {
                    var tag = run >= 2 ? "strong" : "em";
                    var use = run >= 2 ? 2 : 1;
                    html.Append('<').Append(tag).Append('>')
                        .Append(RenderInline(text[(i + use)..close]))
                        .Append("</").Append(tag).Append('>');
                    i = close + use;
                    continue;
                }
            }
            if (c == '&')
            {
                var semi = text.IndexOf(';', i + 1);
                if (semi > i && semi - i <= 12 && IsEntity(text, i, semi))
                {
                    html.Append(text[i..(semi + 1)]);
                    i = semi + 1;
                    continue;
                }
            }
            html.Append(Escape(c));
            i++;
        }
        return html.ToString();
    }

    /// <summary>Try to render a [label](destination "title") (or ![...]) construct at pos.</summary>
    private static bool TryRenderLink(string text, int pos, StringBuilder html, bool image)
    {
        var labelEnd = FindMatchingBracket(text, pos);
        if (labelEnd < 0)
        {
            return false;
        }
        if (labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
        {
            return false;
        }
        var closeParen = text.IndexOf(')', labelEnd + 2);
        if (closeParen < 0)
        {
            return false;
        }
        var dest = text[(labelEnd + 2)..closeParen].Trim();
        var title = "";
        // [label](url "title") / (url 'title')
        var titleMatch = Regex.Match(dest, @"^(?<dest>\S+)[ \t]+(?<q>[""'])(?<title>.*?)\k<q>[ \t]*$");
        if (titleMatch.Success)
        {
            dest = titleMatch.Groups["dest"].Value;
            title = titleMatch.Groups["title"].Value;
        }
        if (dest.StartsWith('<') && dest.EndsWith('>') && dest.Length >= 2)
        {
            dest = dest[1..^1];
        }
        var inner = RenderInline(text[(pos + 1)..labelEnd]);
        if (image)
        {
            html.Append("<img src=\"").Append(Attr(dest)).Append("\" alt=\"")
                .Append(Attr(StripHtml(inner))).Append('"');
            if (title.Length != 0)
            {
                html.Append(" title=\"").Append(Attr(title)).Append('"');
            }
            html.Append(" />");
        }
        else
        {
            html.Append("<a href=\"").Append(Attr(dest)).Append('"');
            if (title.Length != 0)
            {
                html.Append(" title=\"").Append(Attr(title)).Append('"');
            }
            html.Append('>').Append(inner).Append("</a>");
        }
        return true;
    }

    private static int AdvanceLink(string text, int pos)
    {
        var labelEnd = FindMatchingBracket(text, pos);
        var closeParen = text.IndexOf(')', labelEnd + 2);
        return closeParen + 1;
    }

    private static int FindMatchingBracket(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static int RunLength(string text, int pos, char c)
    {
        var run = 0;
        while (pos + run < text.Length && text[pos + run] == c)
        {
            run++;
        }
        return run;
    }

    private static int FindCodeSpanClose(string text, int from, int run)
    {
        var i = from;
        while (i <= text.Length - run)
        {
            if (text[i] == '`')
            {
                var r = RunLength(text, i, '`');
                if (r == run)
                {
                    return i;
                }
                i += r;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static int FindEmphasisClose(string text, int from, char c, int run)
    {
        var use = run >= 2 ? 2 : 1;
        for (var i = from; i <= text.Length - use; i++)
        {
            if (text[i] != c)
            {
                continue;
            }
            var closeRun = RunLength(text, i, c);
            if (closeRun >= use && (i == 0 || text[i - 1] != ' '))
            {
                return i;
            }
            i += closeRun - 1;
        }
        return -1;
    }

    private static bool IsValidHtmlTag(string text, int open, int close)
    {
        var inner = text[(open + 1)..close];
        return HtmlLineRegex().IsMatch("<" + inner + ">") || inner.StartsWith('/') || inner.StartsWith('!') ||
               inner.StartsWith('?');
    }

    private static bool IsEntity(string text, int start, int semi)
    {
        if (semi <= start + 1)
        {
            return false;
        }
        var body = text[(start + 1)..semi];
        if (body.StartsWith('#'))
        {
            var digits = body[1..];
            if (digits.StartsWith('x') || digits.StartsWith('X'))
            {
                return digits.Length > 1 && digits[1..].All(char.IsAsciiHexDigit);
            }
            return digits.Length > 0 && digits.All(char.IsAsciiDigit);
        }
        return body.Length > 0 && body.All(c => char.IsAsciiLetterOrDigit(c));
    }

    private static string StripHtml(string s)
    {
        return Regex.Replace(s, "<[^>]*>", "");
    }

    private static bool IsAsciiPunctuation(char c)
    {
        return "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~".Contains(c);
    }

    // ------------------------------------------------------------- escaping

    private static string Escape(char c)
    {
        return c switch
        {
            '&' => "&amp;",
            '<' => "&lt;",
            '>' => "&gt;",
            '"' => "&quot;",
            _ => c.ToString(),
        };
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    private static string Attr(string s)
    {
        return Escape(s);
    }
}
