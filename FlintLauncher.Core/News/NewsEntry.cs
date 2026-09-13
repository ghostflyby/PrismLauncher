// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/news/NewsEntry.{h,cpp}: one post of the launcher news feed. The feed is
// Atom-shaped; the C++ reads the child elements "title", "content" and "id" (the id doubles
// as the link, see NewsEntry::fromXmlElement).
using System.Xml;

namespace FlintLauncher.Core.News;

public sealed class NewsEntry
{
    public const string DefaultTitle = "Untitled";
    public const string DefaultContent = "No content.";

    public NewsEntry() : this(DefaultTitle, DefaultContent, "")
    {
    }

    public NewsEntry(string title, string content, string link)
    {
        Title = title;
        Content = content;
        Link = link;
    }

    /// <summary>The post title.</summary>
    public string Title { get; set; }

    /// <summary>The post's content. May contain HTML.</summary>
    public string Content { get; set; }

    /// <summary>URL to the post.</summary>
    public string Link { get; set; }

    /// <summary>
    /// Port of NewsEntry::fromXmlElement: reads the entry element the reader is currently
    /// positioned on (a StartElement) and fills an entry from it. Like upstream this never
    /// fails; missing children keep their defaults ("Untitled" / "No content." / "").
    /// </summary>
    public static NewsEntry FromXmlElement(XmlReader entryReader)
    {
        using var subtree = entryReader.ReadSubtree();
        var entry = new NewsEntry();
        var title = new ChildValue(DefaultTitle);
        var content = new ChildValue(DefaultContent);
        var link = new ChildValue("");
        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            // childValue() takes the FIRST element with the matching name anywhere inside the entry.
            if (subtree.Name == "title" && !title.Found)
            {
                title = new ChildValue(ReadInnerText(subtree), Found: true);
            }
            else if (subtree.Name == "content" && !content.Found)
            {
                content = new ChildValue(ReadInnerText(subtree), Found: true);
            }
            else if (subtree.Name == "id" && !link.Found)
            {
                link = new ChildValue(ReadInnerText(subtree), Found: true);
            }
        }
        entry.Title = title.Value;
        entry.Content = content.Value;
        entry.Link = link.Value;
        return entry;
    }

    /// <summary>
    /// Concatenated text of the element's whole subtree (port of QDomElement::text(), which
    /// keeps reading descendants' text nodes). The reader is on the StartElement and ends on
    /// the matching EndElement.
    /// </summary>
    private static string ReadInnerText(XmlReader reader)
    {
        var text = "";
        var depth = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    depth++;
                    break;
                case XmlNodeType.EndElement:
                    depth--;
                    if (depth < 0)
                    {
                        return text;
                    }
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    text += reader.Value;
                    break;
            }
        }
        return text;
    }

    private readonly record struct ChildValue(string Value, bool Found = false);
}
