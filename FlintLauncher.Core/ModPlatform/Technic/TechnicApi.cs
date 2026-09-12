// SPDX-License-Identifier: GPL-3.0-only
// Port of the Technic Platform API surface used by the Technic page/model
// (ui/pages/modplatform/technic/TechnicModel.cpp + TechnicPage.cpp + TechnicData.h):
// trending/search/single-pack URL building with the ?build= parameter, the search result
// parsing (the "vanilla" slug exclusion, the icon/"secondary" logo handling), the pack
// metadata parsing with the url-vs-solder detection and the Solder build listing.
// BuildConfig.TECHNIC_API_BASE_URL / TECHNIC_API_BUILD become settable hooks (tests restore
// them — see TechnicTests for the [Collection] pattern).
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

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

public static class TechnicApi
{
    /// <summary>BuildConfig.TECHNIC_API_BASE_URL (settable; tests restore it).</summary>
    public static string BaseUrl { get; set; } = FlintConfig.Default.TechnicApiBaseUrl;

    /// <summary>BuildConfig.TECHNIC_API_BUILD (settable; tests restore it).</summary>
    public static string ApiBuild { get; set; } = FlintConfig.Default.TechnicApiBuild;

    // ------------------------------------------------------------------ URL builders

    /// <summary>Port of ListModel::performSearch's URL selection: empty term -> trending,
    /// a modpack API url (or "#slug") -> single, everything else -> search.</summary>
    public static (string Url, TechnicSearchMode Mode) SearchUrl(string searchTerm)
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
    public static string PackMetadataUrl(string slug) => $"{BaseUrl}modpack/{slug}?build={ApiBuild}";

    /// <summary>TechnicPage::metadataLoaded's Solder pack (build listing) URL.</summary>
    public static string SolderPackUrl(string solderUrl, string slug) => $"{solderUrl}/modpack/{slug}";

    /// <summary>SolderPackInstallTask::executeTask's build URL.</summary>
    public static string SolderBuildUrl(string solderUrl, string slug, string build) =>
        $"{solderUrl}/modpack/{slug}/{build}";

    // ------------------------------------------------------------------ parsers

    /// <summary>Port of ListModel::searchRequestFinished. Returns the parsed packs; an empty
    /// list mirrors the C++ parse-failure / "error" payload paths (nothing is appended).</summary>
    public static List<TechnicModpack> LoadSearchResults(string response, TechnicSearchMode mode)
    {
        var newList = new List<TechnicModpack>();
        JsonObject root;
        try
        {
            using var doc = JsonUtils.RequireDocument(response, "JSON response from Technic");
            root = TechnicJson.RequireObject(doc);
        }
        catch (JsonParseException)
        {
            return newList;
        }

        try
        {
            switch (mode)
            {
                case TechnicSearchMode.List:
                {
                    foreach (var technicPackRaw in TechnicJson.RequireArray(root, "modpacks"))
                    {
                        var technicPackObject = (JsonObject)(technicPackRaw
                            ?? throw new JsonParseException("modpacks entry is not an object"));
                        var pack = new TechnicModpack
                        {
                            Name = JsonUtils.RequireString(technicPackObject, "name"),
                            Slug = JsonUtils.RequireString(technicPackObject, "slug"),
                        };
                        if (pack.Slug == "vanilla")
                        {
                            continue;
                        }

                        var rawUrl = technicPackObject.TryGetPropertyValue("iconUrl", out var iconNode)
                            && iconNode is JsonValue iconValue && iconValue.TryGetValue<string>(out var iconUrl)
                            ? iconUrl : "null";
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
                    if (root.ContainsKey("error"))
                    {
                        // Invalid API url
                        break;
                    }

                    var pack = new TechnicModpack
                    {
                        Name = JsonUtils.RequireString(root, "displayName"),
                        Slug = JsonUtils.RequireString(root, "name"),
                    };

                    if (root.ContainsKey("icon"))
                    {
                        var iconObj = TechnicJson.RequireObject(JsonUtils.RequireObject(root, "icon"), "icon");
                        var iconUrl = JsonUtils.RequireString(iconObj, "url");

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
        JsonObject obj;
        try
        {
            using var doc = JsonUtils.RequireDocument(response, "JSON response from Technic");
            obj = TechnicJson.RequireObject(doc);
        }
        catch (JsonParseException)
        {
            return false;
        }

        static string? StringOrNothing(JsonObject o, string key)
        {
            return o.TryGetPropertyValue(key, out var node) && node is JsonValue value
                && value.TryGetValue<string>(out var str) ? str : null;
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

        current.MinecraftVersion = obj.TryGetPropertyValue("minecraft", out var mcNode) && mcNode is JsonValue mcValue
            && mcValue.TryGetValue<string>(out var mc) ? mc : "";
        current.WebsiteUrl = obj.TryGetPropertyValue("platformUrl", out var platformNode) && platformNode is JsonValue platformValue
            && platformValue.TryGetValue<string>(out var platform) ? platform : "";
        current.Author = obj.TryGetPropertyValue("user", out var userNode) && userNode is JsonValue userValue
            && userValue.TryGetValue<string>(out var user) ? user : "";
        current.Description = obj.TryGetPropertyValue("description", out var descNode) && descNode is JsonValue descValue
            && descValue.TryGetValue<string>(out var description) ? description : "";
        current.CurrentVersion = obj.TryGetPropertyValue("version", out var versionNode) && versionNode is JsonValue versionValue
            && versionValue.TryGetValue<string>(out var version) ? version : "";
        current.MetadataLoaded = true;
        return true;
    }

    /// <summary>Port of TechnicPage::onSolderLoaded's parsing (TechnicSolder::loadPack).
    /// Returns null on parse errors (the C++ falls back to the current version).</summary>
    public static SolderPack? LoadSolderPack(string response)
    {
        JsonObject obj;
        try
        {
            using var doc = JsonUtils.RequireDocument(response, "JSON response from Solder");
            obj = TechnicJson.RequireObject(doc);
        }
        catch (JsonParseException)
        {
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
