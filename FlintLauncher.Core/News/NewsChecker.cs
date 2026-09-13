// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/news/NewsChecker.{h,cpp}: download the launcher's news feed (cached like
// the C++ Net::Request::makeCached through the meta cache) and parse its Atom entries.
//
// Deviations: the C++ parse goes through QDomDocument::setContent(); the port uses XmlReader
// and maps XmlException's message/line/column into the same error string shape. The C++
// reloadNews() is a slot starting a NetJob; the port is a FlintTask started with StartAsync()
// (the already-running guard is Task::start's).
using System.Xml;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.News;

public sealed class NewsChecker : FlintTask
{
    private const string CacheBaseId = "feed";
    private const string CacheResourcePath = "feed.xml";

    private readonly string _feedUrl;
    private readonly HttpMetaCache? _injectedCache;
    private HttpMetaCache? _ownedCache;
    private readonly List<NewsEntry> _newsEntries = [];
    private bool _loadedNews;
    private string _lastLoadError = "";

    /// <summary>Port of NewsChecker(network, feedUrl). The cache defaults to a temporary-dir
    /// one mirroring APPLICATION->metacache(); pass one to control the location (tests).</summary>
    public NewsChecker(string feedUrl, HttpMetaCache? cache = null)
    {
        _feedUrl = feedUrl;
        _injectedCache = cache;
    }

    /// <summary>Port of getLastLoadErrorMsg().</summary>
    public string LastLoadErrorMsg => _lastLoadError;

    /// <summary>Port of isNewsLoaded().</summary>
    public bool IsNewsLoaded => _loadedNews;

    /// <summary>Port of isLoadingNews().</summary>
    public bool IsLoadingNews => IsRunning;

    /// <summary>Port of getNewsEntries().</summary>
    public IReadOnlyList<NewsEntry> NewsEntries => _newsEntries;

    /// <summary>Port of reloadNews().</summary>
    public Task ReloadNewsAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var cache = _injectedCache ?? GetOrCreateOwnedCache();
        if (cache.GetBasePath(CacheBaseId) == "")
        {
            cache.AddBase(CacheBaseId, Path.Combine(Path.GetTempPath(), "flint-news-cache"));
        }

        var entry = cache.ResolveEntry(CacheBaseId, CacheResourcePath);
        var request = NetRequest.MakeCached(cache, new Uri(_feedUrl), entry);
        await request.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!request.WasSuccessful)
        {
            Fail($"Failed to load news RSS feed:\n{request.FailReason}");
            return;
        }

        // Port of rssDownloadFinished(): parse the cached feed file. Upstream opens the file and
        // only feeds setContent() when the open succeeded — an unreadable cache file yields an
        // EMPTY document and therefore succeeds with zero entries, which the port mirrors by
        // parsing the empty string.
        string content = "";
        try
        {
            content = await File.ReadAllTextAsync(entry.GetFullPath(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  or System.Security.SecurityException or ArgumentException)
        {
        }

        var entries = new List<NewsEntry>();
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(new StringReader(content), settings);
            // The C++ walks doc.elementsByTagName("entry") — every "entry" element in the document.
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "entry")
                {
                    entries.Add(NewsEntry.FromXmlElement(reader));
                }
            }
        }
        catch (XmlException e)
        {
            Fail($"Error parsing RSS feed XML. {e.Message} at {e.LineNumber}:{e.LinePosition}.");
            return;
        }

        _newsEntries.Clear();
        _newsEntries.AddRange(entries);
        Succeed();
    }

    /// <summary>Port of succeed(): clears the error and marks the news loaded.</summary>
    private void Succeed()
    {
        _lastLoadError = "";
        _loadedNews = true;
        EmitSucceeded();
    }

    /// <summary>Port of fail(errorMsg).</summary>
    private void Fail(string errorMsg)
    {
        _lastLoadError = errorMsg;
        EmitFailed(errorMsg);
    }

    private HttpMetaCache GetOrCreateOwnedCache()
    {
        if (_ownedCache is null)
        {
            _ownedCache = new HttpMetaCache();
        }
        return _ownedCache;
    }
}
