// SPDX-License-Identifier: GPL-3.0-only
// Tests for the news feed port (launcher/news/NewsChecker.{h,cpp} + NewsEntry.{h,cpp}): the
// Atom entry parsing (title/content/id child values with the QDomElement::text() concatenation
// semantics), the cached-download flow through HttpMetaCache and the exact failure messages.
using System.Xml;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.News;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace Flint.Tests;

public class NewsTests : IDisposable
{
    private readonly PeripheralStubServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-news-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>A cache rooted in the temp dir (the C++ APPLICATION->metacache() stand-in).</summary>
    private HttpMetaCache MakeCache()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cache"));
        var cache = new HttpMetaCache(Path.Combine(_dir, "metacache"));
        cache.AddBase("feed", Path.Combine(_dir, "cache"));
        return cache;
    }

    private const string AtomFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Flint news</title>
          <entry>
            <title>Flint 0.2.0</title>
            <link href="https://prismlauncher.org/news/flint-0-2-0" />
            <id>tag:prismlauncher.org,2026:flint-0-2-0</id>
            <content type="html">&lt;p&gt;Hello &lt;b&gt;world&lt;/b&gt;&lt;/p&gt;</content>
            <updated>2026-09-01T00:00:00Z</updated>
          </entry>
          <entry>
            <title>Second post</title>
            <id>tag:prismlauncher.org,2026:second</id>
            <content>plain text body</content>
          </entry>
          <entry>
            <id>tag:prismlauncher.org,2026:minimal</id>
          </entry>
        </feed>
        """;

    // ---------------------------------------------------------------- NewsEntry

    [Fact]
    public void NewsEntry_Defaults_Match_The_Cpp_Constructor()
    {
        var entry = new NewsEntry();
        Assert.Equal("Untitled", entry.Title);
        Assert.Equal("No content.", entry.Content);
        Assert.Equal("", entry.Link);
    }

    [Fact]
    public void FromXmlElement_Reads_Title_Content_And_Id_As_Link()
    {
        var xml = """
            <entry>
              <title>T</title>
              <id>tag:link</id>
              <content type="html">&lt;b&gt;hi&lt;/b&gt;</content>
            </entry>
            """;
        var entry = ParseEntry(xml);
        Assert.Equal("T", entry.Title);
        Assert.Equal("tag:link", entry.Link);
        Assert.Equal("<b>hi</b>", entry.Content);
    }

    [Fact]
    public void FromXmlElement_Missing_Children_Keep_Defaults()
    {
        var entry = ParseEntry("<entry><id>x</id></entry>");
        Assert.Equal("Untitled", entry.Title);
        Assert.Equal("No content.", entry.Content);
        Assert.Equal("x", entry.Link);
    }

    // QDomElement::text() concatenates ALL descendant text nodes.
    [Fact]
    public void FromXmlElement_Concatenates_Nested_Text()
    {
        var entry = ParseEntry("""
            <entry>
              <content>alpha <em>beta</em> gamma</content>
            </entry>
            """);
        Assert.Equal("alpha beta gamma", entry.Content);
    }

    // childValue() takes the FIRST element with the matching name.
    [Fact]
    public void FromXmlElement_Takes_The_First_Matching_Child()
    {
        var entry = ParseEntry("<entry><title>first</title><title>second</title></entry>");
        Assert.Equal("first", entry.Title);
    }

    private static NewsEntry ParseEntry(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "entry")
            {
                return NewsEntry.FromXmlElement(reader);
            }
        }
        throw new InvalidOperationException("no <entry> element in test xml");
    }

    // ---------------------------------------------------------------- NewsChecker

    [Fact]
    public async Task ReloadNews_Downloads_Caches_And_Parses()
    {
        _server.Responder = _ => (200, AtomFeed, "OK");
        using var cache = MakeCache();
        var checker = new NewsChecker(_server.BaseUrl + "/feed.xml", cache);

        NewsChecker? loaded = null;
        checker.Succeeded += t => loaded = (NewsChecker)t;

        await checker.ReloadNewsAsync();

        Assert.Equal(TaskState.Succeeded, checker.State);
        Assert.True(checker.IsNewsLoaded);
        Assert.False(checker.IsLoadingNews);
        Assert.Equal("", checker.LastLoadErrorMsg);
        Assert.NotNull(loaded);
        Assert.Equal(3, checker.NewsEntries.Count);

        Assert.Equal("Flint 0.2.0", checker.NewsEntries[0].Title);
        Assert.Equal("tag:prismlauncher.org,2026:flint-0-2-0", checker.NewsEntries[0].Link);
        Assert.Equal("<p>Hello <b>world</b></p>", checker.NewsEntries[0].Content);

        Assert.Equal("Second post", checker.NewsEntries[1].Title);
        Assert.Equal("tag:prismlauncher.org,2026:second", checker.NewsEntries[1].Link);

        // the minimal entry keeps the C++ defaults
        Assert.Equal("Untitled", checker.NewsEntries[2].Title);
        Assert.Equal("No content.", checker.NewsEntries[2].Content);

        // the feed went through the meta cache ("feed"/"feed.xml"), like Net::Request::makeCached
        Assert.True(File.Exists(Path.Combine(_dir, "cache", "feed.xml")));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task RssDownload_Failed_Sets_The_Cpp_Message()
    {
        _server.Responder = _ => (500, "server error", "Internal Server Error");
        using var cache = MakeCache();
        var checker = new NewsChecker(_server.BaseUrl + "/feed.xml", cache);

        await checker.ReloadNewsAsync();

        Assert.Equal(TaskState.Failed, checker.State);
        Assert.False(checker.IsNewsLoaded);
        Assert.StartsWith("Failed to load news RSS feed:\n", checker.LastLoadErrorMsg);
        Assert.Empty(checker.NewsEntries);
    }

    [Fact]
    public async Task Broken_Xml_Uses_The_SetContent_Error_Format()
    {
        // QString("Error parsing RSS feed XML. %1 at %2:%3.")
        _server.Responder = _ => (200, "<feed><entry><title>unclosed</entry></feed>", "OK");
        using var cache = MakeCache();
        var checker = new NewsChecker(_server.BaseUrl + "/feed.xml", cache);

        await checker.ReloadNewsAsync();

        Assert.Equal(TaskState.Failed, checker.State);
        Assert.Matches("""Error parsing RSS feed XML\. .+ at \d+:\d+\.""", checker.LastLoadErrorMsg);
        Assert.Empty(checker.NewsEntries);
    }

    // The C++ only calls setContent() when the cached file opened; an EMPTY cache file yields an
    // empty document and therefore succeeds with zero entries (no parse error is raised).
    [Fact]
    public async Task Empty_Cached_Feed_Fails_With_The_Parse_Error()
    {
        _server.Responder = _ => (200, "", "OK");
        using var cache = MakeCache();
        var checker = new NewsChecker(_server.BaseUrl + "/feed.xml", cache);

        await checker.ReloadNewsAsync();

        // C++ semantics: an OPENABLE but invalid (empty) feed fails XML parsing with the
        // "Error parsing RSS feed XML." message; succeed-with-zero-entries only happens when
        // the cache file cannot be opened at all.
        Assert.Equal(TaskState.Failed, checker.State);
        Assert.StartsWith("Error parsing RSS feed XML.", checker.LastLoadErrorMsg);
        Assert.False(checker.IsNewsLoaded);
        Assert.Empty(checker.NewsEntries);
    }

    [Fact]
    public async Task Reload_After_Finish_Runs_Again()
    {
        _server.Responder = _ => (200, AtomFeed, "OK");
        using var cache = MakeCache();
        var checker = new NewsChecker(_server.BaseUrl + "/feed.xml", cache);
        await checker.ReloadNewsAsync();

        // reloadNews() on a finished checker runs again (the guard only blocks a RUNNING job)
        _server.Responder = _ => (200, "<feed><entry><title>fresh</title><id>f</id></entry></feed>", "OK");
        await checker.ReloadNewsAsync();
        Assert.Equal(TaskState.Succeeded, checker.State);
        Assert.Single(checker.NewsEntries);
        Assert.Equal("fresh", checker.NewsEntries[0].Title);
    }
}
