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
// helpers and stay static; the wire shapes deserialize through the source-generated
// TechnicJsonContext (the Solder models carry their annotations themselves).
//
// Wire-model notes:
//  - the single-pack response's "error" member is an ERROR *presence* sentinel (the key
//    counts even when it holds JSON null) — captured through [JsonExtensionData] instead
//    of a property, so ContainsKey keeps the exact QJsonObject::contains semantics;
//  - a missing/other-kind iconUrl falls back to the literal "null" logo sentinel.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.Technic;

/// <summary>The trending/search list root.</summary>
public sealed record TechnicSearchListJson(
    List<TechnicListEntryJson>? Modpacks);

/// <summary>One trending/search entry; the iconUrl member keeps the QJsonValue
/// toString("null") fallback — a missing/null/non-string value carries the literal "null"
/// sentinel (the caller maps it to the null-logo placeholder), hence the raw element.</summary>
public sealed record TechnicListEntryJson(
    string? Name,
    string? Slug,
    JsonElement? IconUrl);

/// <summary>The single-pack response; unbound members (the "error" sentinel above all)
/// land in Extension. Not a positional record: an extension-data property cannot bind to a
/// deserialization constructor parameter.</summary>
public sealed class TechnicSinglePackJson
{
    public string? DisplayName { get; init; }

    public string? Name { get; init; }

    public TechnicIconJson? Icon { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extension { get; set; } = new();
}

public sealed record TechnicIconJson(
    string? Url);

/// <summary>The pack metadata response: a string "url" (direct zip) wins over a string
/// "solder" (Solder API root); neither fails the load. The remaining members keep the
/// QJsonValue toString("") fallback (a missing/junk value becomes "").</summary>
public sealed record TechnicMetadataJson(
    string? Url,
    string? Solder,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? Minecraft,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? PlatformUrl,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? User,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? Description,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TechnicSearchListJson))]
[JsonSerializable(typeof(TechnicSinglePackJson))]
[JsonSerializable(typeof(TechnicMetadataJson))]
[JsonSerializable(typeof(TechnicPackProcessor.TechnicVersionJson))]
[JsonSerializable(typeof(SolderPack))]
[JsonSerializable(typeof(SolderPackBuild))]
internal sealed partial class TechnicJsonContext : JsonSerializerContext;

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

    /// <summary>Document parse with the "{what}: Error parsing JSON: ..." wrapping; a JSON
    /// null document parses to null (the callers' failure paths treat it like the rest).</summary>
    private static T? DeserializeDocument<T>(string json, string what, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException e)
        {
            throw new JsonParseException(what + ": Error parsing JSON: " + e.Message);
        }
    }

    private static string RequireMember(string? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not a string");

    private static List<T> RequireMember<T>(List<T>? value, string key) where T : class =>
        value ?? throw new JsonParseException("'" + key + "' is not an array");

    /// <summary>Port of ListModel::searchRequestFinished. Returns the parsed packs; an empty
    /// list mirrors the C++ parse-failure / "error" payload paths (nothing is appended).</summary>
    public static List<TechnicModpack> LoadSearchResults(string response, TechnicSearchMode mode)
    {
        var newList = new List<TechnicModpack>();
        try
        {
            switch (mode)
            {
                case TechnicSearchMode.List:
                {
                    var root = DeserializeDocument<TechnicSearchListJson>(response, "JSON response from Technic",
                        TechnicJsonContext.Default.TechnicSearchListJson);
                    foreach (var raw in RequireMember(root?.Modpacks, "modpacks"))
                    {
                        var pack = new TechnicModpack
                        {
                            Name = RequireMember(raw.Name, "name"),
                            Slug = RequireMember(raw.Slug, "slug"),
                        };
                        if (pack.Slug == "vanilla")
                        {
                            continue;
                        }

                        var rawUrl = raw.IconUrl is { ValueKind: JsonValueKind.String } iconUrlNode
                            ? iconUrlNode.GetString() ?? "null"
                            : "null";
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
                    var root = DeserializeDocument<TechnicSinglePackJson>(response, "JSON response from Technic",
                        TechnicJsonContext.Default.TechnicSinglePackJson);
                    if (root?.Extension is { Count: > 0 } extension && extension.ContainsKey("error"))
                    {
                        // Invalid API url
                        break;
                    }

                    var pack = new TechnicModpack
                    {
                        Name = RequireMember(root?.DisplayName, "displayName"),
                        Slug = RequireMember(root?.Name, "name"),
                    };

                    if (root?.Icon?.Url is { } iconUrl)
                    {
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
    /// fills url/isSolder (a string "url" wins over a string "solder"; neither fails the
    /// load), minecraft, platformUrl, user, description and version. Returns false when the
    /// payload has neither a string url nor a string solder key (upstream bails out with a
    /// warning).
    /// </summary>
    public static bool LoadPackMetadata(string response, TechnicModpack current)
    {
        TechnicMetadataJson? obj;
        try
        {
            obj = DeserializeDocument<TechnicMetadataJson>(response, "JSON response from Technic",
                TechnicJsonContext.Default.TechnicMetadataJson);
        }
        catch (JsonParseException)
        {
            return false;
        }

        if (obj?.Url is { } url)
        {
            current.Url = url;
        }
        else if (obj?.Solder is { } solderUrl)
        {
            current.Url = solderUrl;
            current.IsSolder = true;
        }
        else
        {
            return false;
        }

        current.MinecraftVersion = obj.Minecraft ?? "";
        current.WebsiteUrl = obj.PlatformUrl ?? "";
        current.Author = obj.User ?? "";
        current.Description = obj.Description ?? "";
        current.CurrentVersion = obj.Version ?? "";
        current.MetadataLoaded = true;
        return true;
    }

    /// <summary>Port of TechnicPage::onSolderLoaded's parsing (TechnicSolder::loadPack).
    /// Returns null on parse errors (the C++ falls back to the current version).</summary>
    public static SolderPack? LoadSolderPack(string response)
    {
        try
        {
            return DeserializeDocument<SolderPack>(response, "JSON response from Solder",
                TechnicJsonContext.Default.SolderPack);
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    /// <summary>Port of TechnicSolder::loadPackBuild (the SolderPackInstallTask entry).
    /// Throws <see cref="JsonParseException"/> with the Solder wrapper on any failure.</summary>
    public static SolderPackBuild LoadSolderPackBuild(string response)
    {
        var build = DeserializeDocument<SolderPackBuild>(response, "JSON response from Solder",
                        TechnicJsonContext.Default.SolderPackBuild)
                    ?? throw new JsonParseException("JSON response from Solder: Error parsing JSON: document is null");

        // JSON null members/elements are not enforced by the binding — surface them like the
        // former Require reads instead of letting them NRE the install task
        if (build.Minecraft is null)
        {
            throw new JsonParseException("'minecraft' is not a string");
        }

        foreach (var mod in build.Mods)
        {
            // a null ELEMENT or a null url both surface as the former RequireObject check
            if (mod?.Url is null)
            {
                throw new JsonParseException("Value is not an object");
            }
        }

        return build;
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
