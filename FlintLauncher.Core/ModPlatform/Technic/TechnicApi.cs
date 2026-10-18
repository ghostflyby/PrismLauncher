// SPDX-License-Identifier: GPL-3.0-only
// Port of the Technic Platform API surface used by the Technic page/model
// (ui/pages/modplatform/technic/TechnicModel.cpp + TechnicPage.cpp + TechnicData.h):
// trending/search/single-pack URL building with the ?build= parameter, the search result
// parsing (the "vanilla" slug exclusion, the icon/"secondary" logo handling), the pack
// metadata parsing with the url-vs-solder detection and the Solder build listing.
//
// BuildConfig.TECHNIC_API_BASE_URL / TECHNIC_API_BUILD became injected instance state:
// BaseUrl / ApiBuild are per-instance (tests construct their own client pointed at a stub);
// the client rides the injected NetStack (LauncherRuntime.Technic — see the Modrinth/Flame
// de-singletoning for the same story). The parsers and the Solder URL builders are pure
// helpers and stay static; reads go through the JsonNode model (see TechnicJson/JsonUtils).

using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.Technic;

/// <summary>Port of Technic::Modpack (TechnicData.h).</summary>
public sealed class TechnicModpack
{
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";
    public string LogoUrl { get; set; } = "";
    public string LogoName { get; set; } = "";

    public bool Broken { get; set; } = true;

    public string Url { get; set; } = "";
    public bool IsSolder { get; set; }
    public string MinecraftVersion { get; set; } = "";

    public bool MetadataLoaded { get; set; }
    public string WebsiteUrl { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string CurrentVersion { get; set; } = "";

    public bool VersionsLoaded { get; set; }
    public string Recommended { get; set; } = "";
    public List<string> Versions { get; } = [];
}

/// <summary>Port of the Technic::ListModel search modes.</summary>
public enum TechnicSearchMode : byte
{
    List,
    Single,
}

public sealed class TechnicApi(NetStack? net = null)
{
    /// <summary>BuildConfig.TECHNIC_API_BASE_URL (per-instance; tests construct a client
    /// pointed at their stub).</summary>
    public string BaseUrl { get; set; } = FlintConfig.Default.TechnicApiBaseUrl;

    /// <summary>BuildConfig.TECHNIC_API_BUILD (per-instance).</summary>
    public string ApiBuild { get; set; } = FlintConfig.Default.TechnicApiBuild;

    /// <summary>The network stack this client's requests go through.</summary>
    public NetStack Net { get; } = net ?? NetStack.Default;

    // ------------------------------------------------------------------ URL builders

    /// <summary>Port of ListModel::performSearch's URL selection: empty term -> trending,
    /// a modpack API url (or "#slug") -> single, everything else -> search.</summary>
    public (string Url, TechnicSearchMode Mode) SearchUrl(string searchTerm)
    {
        string searchUrl;
        TechnicSearchMode mode;
        if (searchTerm.Length == 0)
        {
            searchUrl = $"{BaseUrl}trending?build={ApiBuild}";
            mode = TechnicSearchMode.List;
        }
        else if (searchTerm.StartsWith("http://api.technicpack.net/modpack/", StringComparison.Ordinal))
        {
            searchUrl = $"https://{searchTerm[7..]}?build={ApiBuild}";
            mode = TechnicSearchMode.Single;
        }
        else if (searchTerm.StartsWith("https://api.technicpack.net/modpack/", StringComparison.Ordinal))
        {
            searchUrl = $"{searchTerm}?build={ApiBuild}";
            mode = TechnicSearchMode.Single;
        }
        else if (searchTerm.StartsWith("#", StringComparison.Ordinal))
        {
            searchUrl = $"https://api.technicpack.net/modpack/{searchTerm[1..]}?build={ApiBuild}";
            mode = TechnicSearchMode.Single;
        }
        else
        {
            searchUrl = $"{BaseUrl}search?build={ApiBuild}&q={Uri.EscapeDataString(searchTerm)}";
            mode = TechnicSearchMode.List;
        }

        return (searchUrl, mode);
    }

    /// <summary>TechnicPage's per-slug pack metadata URL.</summary>
    public string PackMetadataUrl(string slug) => $"{BaseUrl}modpack/{slug}?build={ApiBuild}";

    /// <summary>TechnicPage::metadataLoaded's Solder pack (build listing) URL.</summary>
    public static string SolderPackUrl(string solderUrl, string slug) => $"{solderUrl}/modpack/{slug}";

    /// <summary>SolderPackInstallTask::executeTask's build URL.</summary>
    public static string SolderBuildUrl(string solderUrl, string slug, string build) =>
        $"{solderUrl}/modpack/{slug}/{build}";

    // ------------------------------------------------------------------ parsers

    // node-side member readers (the MetaJsonFormat/FlameJson convention: JsonNode? parameters
    // so the node-family overloads bind instead of the (JsonObject, key, what) element bridges)

    /// <summary>QJsonObject::contains(key) equivalent: true even when the member holds JSON null.</summary>
    private static bool Has(JsonNode? node, string key) => node is JsonObject map && map.ContainsKey(key);

    /// <summary>obj["key"] equivalent: null node when missing or not an object.</summary>
    private static JsonNode? Ptr(JsonNode? node, string key) => node is JsonObject map ? map[key] : null;

    /// <summary>Port of ListModel::searchRequestFinished. Returns the parsed packs; an empty
    /// list mirrors the C++ parse-failure / "error" payload paths (nothing is appended).</summary>
    public static List<TechnicModpack> LoadSearchResults(string response, TechnicSearchMode mode)
    {
        var newList = new List<TechnicModpack>();
        JsonNode root;
        try
        {
            root = TechnicJson.RequireDocument(response, "JSON response from Technic");
        }
        catch (JsonParseException)
        {
            return newList;
        }

        if (root is not JsonObject)
        {
            // the old Json::requireObject(doc) "Document is not an object" path
            return newList;
        }

        try
        {
            switch (mode)
            {
                case TechnicSearchMode.List:
                {
                    foreach (var technicPackRaw in JsonUtils.RequireArray(root, "modpacks", "'modpacks'"))
                    {
                        // JsonNode-typed receiver so the reads below hit the node family (not
                        // the (JsonObject, key, what) element bridges); the downcast keeps the
                        // old hard-cast behavior for a non-object entry
                        JsonNode technicPack = (JsonObject)(technicPackRaw
                                                            ?? throw new JsonParseException("modpacks entry is not an object"));
                        var pack = new TechnicModpack
                        {
                            Name = JsonUtils.RequireString(technicPack, "name", "'name'"),
                            Slug = JsonUtils.RequireString(technicPack, "slug", "'slug'"),
                        };
                        if (pack.Slug == "vanilla")
                        {
                            continue;
                        }

                        var rawUrl = JsonUtils.EnsureString(technicPack["iconUrl"], "null");
                        if (rawUrl == "null")
                        {
                            pack.LogoUrl = "null";
                            pack.LogoName = "null";
                        }
                        else
                        {
                            pack.LogoUrl = rawUrl;
                            pack.LogoName = pack.Slug + "." + UrlFileNameSuffix(rawUrl);
                        }

                        pack.Broken = false;
                        newList.Add(pack);
                    }

                    break;
                }
                case TechnicSearchMode.Single:
                {
                    if (Has(root, "error"))
                    {
                        // Invalid API url
                        break;
                    }

                    var pack = new TechnicModpack
                    {
                        Name = JsonUtils.RequireString(root, "displayName", "'displayName'"),
                        Slug = JsonUtils.RequireString(root, "name", "'name'"),
                    };

                    if (Has(root, "icon"))
                    {
                        JsonNode iconObj = JsonUtils.RequireObject(root, "icon", "'icon'");
                        var iconUrl = JsonUtils.RequireString(iconObj, "url", "'url'");

                        pack.LogoUrl = iconUrl;
                        pack.LogoName = pack.Slug + "." + UrlFileNameSuffix(iconUrl);
                    }
                    else
                    {
                        pack.LogoUrl = "null";
                        pack.LogoName = "null";
                    }

                    pack.Broken = false;
                    newList.Add(pack);
                    break;
                }
            }
        }
        catch (JsonParseException)
        {
            return [];
        }

        return newList;
    }

    /// <summary>
    /// Port of TechnicPage::onSlectionChanged's metadata step (the metadataLoaded JSON):
    /// fills url/isSolder (the "solder" field presence marks a Solder pack), minecraft,
    /// platformUrl, user, description and version. Returns false when the payload has
    /// neither a string url nor a string solder key (upstream bails out with a warning).
    /// </summary>
    public static bool LoadPackMetadata(string response, TechnicModpack current)
    {
        JsonNode obj;
        try
        {
            obj = TechnicJson.RequireDocument(response, "JSON response from Technic");
        }
        catch (JsonParseException)
        {
            return false;
        }

        if (obj is not JsonObject)
        {
            // the old Json::requireObject(doc) "Document is not an object" path
            return false;
        }

        static string? StringOrNothing(JsonNode? o, string key)
        {
            return Ptr(o, key) is JsonValue value && value.TryGetValue<string>(out var str) ? str : null;
        }

        var url = StringOrNothing(obj, "url");
        if (url is not null)
        {
            current.Url = url;
        }
        else
        {
            var solderUrl = StringOrNothing(obj, "solder");
            if (solderUrl is null)
            {
                return false;
            }

            current.Url = solderUrl;
            current.IsSolder = true;
        }

        current.MinecraftVersion = JsonUtils.EnsureString(Ptr(obj, "minecraft"));
        current.WebsiteUrl = JsonUtils.EnsureString(Ptr(obj, "platformUrl"));
        current.Author = JsonUtils.EnsureString(Ptr(obj, "user"));
        current.Description = JsonUtils.EnsureString(Ptr(obj, "description"));
        current.CurrentVersion = JsonUtils.EnsureString(Ptr(obj, "version"));
        current.MetadataLoaded = true;
        return true;
    }

    /// <summary>Port of TechnicPage::onSolderLoaded's parsing (TechnicSolder::loadPack).
    /// Returns null on parse errors (the C++ falls back to the current version).</summary>
    public static SolderPack? LoadSolderPack(string response)
    {
        JsonNode obj;
        try
        {
            obj = TechnicJson.RequireDocument(response, "JSON response from Solder");
        }
        catch (JsonParseException)
        {
            return null;
        }

        if (obj is not JsonObject)
        {
            // the old Json::requireObject(doc) "Document is not an object" path
            return null;
        }

        try
        {
            var pack = new SolderPack();
            TechnicSolder.LoadPack(pack, obj);
            return pack;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    /// <summary>QFileInfo(QUrl(url).fileName()).suffix() equivalent: the extension of the
    /// last path segment (without the dot).</summary>
    private static string UrlFileNameSuffix(string url)
    {
        var fileName = url;
        var slash = fileName.LastIndexOf('/');
        if (slash >= 0)
        {
            fileName = fileName[(slash + 1)..];
        }

        // strip a query string, if any
        var query = fileName.IndexOf('?');
        if (query >= 0)
        {
            fileName = fileName[..query];
        }

        var dot = fileName.LastIndexOf('.');
        return dot >= 0 ? fileName[(dot + 1)..] : "";
    }
}
