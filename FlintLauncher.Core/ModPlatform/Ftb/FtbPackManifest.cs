// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/ftb/FTBPackManifest.{h,cpp}: the modpacks.ch-style JSON models of the
// FTB App API — the pack description (Modpack with art/authors/versions/tags) and the
// version payload (Version with targets[] and files[]). Reads go through the JsonNode
// model (single-DOM, the JsonUtils node-side family).
//
// Deviations: the Json::require* calls throw JsonParseException (the C++ JSONValidationError);
// optional fields keep the QJsonValue coercion defaults (toInt() of a missing value -> 0,
// toString() -> ""); Modpack.safeName is a computed property instead of a parsed field.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Ftb;

/// <summary>Port of FTB::Specs.</summary>
public sealed class FtbSpecs
{
    public int Id { get; set; }
    public int Minimum { get; set; }
    public int Recommended { get; set; }
}

/// <summary>Port of FTB::Tag.</summary>
public sealed class FtbTag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>Port of FTB::Art.</summary>
public sealed class FtbArt
{
    public int Id { get; set; }
    public string Url { get; set; } = "";
    public string Type { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Compressed { get; set; }
    public string Sha1 { get; set; } = "";
    public int Size { get; set; }
    public long Updated { get; set; }
}

/// <summary>Port of FTB::Author.</summary>
public sealed class FtbAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Website { get; set; } = "";
    public long Updated { get; set; }
}

/// <summary>Port of FTB::VersionInfo.</summary>
public sealed class FtbVersionInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public long Updated { get; set; }
    public FtbSpecs Specs { get; } = new();
}

/// <summary>Port of FTB::Modpack.</summary>
public sealed partial class FtbModpack
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Featured { get; set; }
    public int Installs { get; set; }
    public int Plays { get; set; }
    public long Updated { get; set; }
    public long Refreshed { get; set; }
    public List<FtbArt> Art { get; } = [];
    public List<FtbAuthor> Authors { get; } = [];
    public List<FtbVersionInfo> Versions { get; } = [];
    public List<FtbTag> Tags { get; } = [];

    /// <summary>Port of the safeName computed at parse time:
    /// name with every non-alphanumeric removed, lowercased + ".png".</summary>
    public string SafeName => SafeNameOf(Name);

    public static string SafeNameOf(string name) =>
        NonAlphanumericRegex().Replace(name, "").ToLowerInvariant() + ".png";

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}

/// <summary>Port of FTB::VersionTarget.</summary>
public sealed class FtbVersionTarget
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public long Updated { get; set; }
}

/// <summary>Port of FTB::VersionFileCurseForge.</summary>
public sealed class FtbVersionFileCurseForge
{
    public int ProjectId { get; set; }
    public int FileId { get; set; }
}

/// <summary>Port of FTB::VersionFile.</summary>
public sealed class FtbVersionFile
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public int Size { get; set; }
    public bool ClientOnly { get; set; }
    public bool ServerOnly { get; set; }
    public bool Optional { get; set; }
    public long Updated { get; set; }
    public FtbVersionFileCurseForge Curseforge { get; } = new();
}

/// <summary>Port of FTB::Version.</summary>
public sealed class FtbVersion
{
    public int Id { get; set; }
    public int Parent { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Installs { get; set; }
    public int Plays { get; set; }
    public long Updated { get; set; }
    public long Refreshed { get; set; }
    public FtbSpecs Specs { get; } = new();
    public List<FtbVersionTarget> Targets { get; } = [];
    public List<FtbVersionFile> Files { get; } = [];
}

public static class FtbPackManifest
{
    // ------------------------------------------------- node-model helpers
    //
    // JsonNode-typed receivers so the reads hit the JsonUtils node family (a JsonObject-typed
    // receiver would silently resolve to the (JsonObject, key, what) element bridges).

    /// <summary>QJsonValue cast to QJsonObject: "Value is not an object" for both a missing
    /// value and a non-object (the array-entry paths).</summary>
    private static JsonObject RequireObject(JsonNode? node)
    {
        return node as JsonObject ?? throw new JsonParseException("Value is not an object");
    }

    /// <summary>Json::requireArray(parent, key) whose entries are objects.</summary>
    private static List<JsonObject> RequireObjectArray(JsonNode parent, string key)
    {
        var result = new List<JsonObject>();
        foreach (var item in JsonUtils.RequireArray(parent, key, "'" + key + "'"))
        {
            result.Add(RequireObject(item));
        }
        return result;
    }

    // ------------------------------------------------------------------- loaders

    private static void LoadSpecs(FtbSpecs s, JsonNode obj)
    {
        s.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        s.Minimum = JsonUtils.RequireInteger(obj, "minimum", "'minimum'");
        s.Recommended = JsonUtils.RequireInteger(obj, "recommended", "'recommended'");
    }

    private static void LoadTag(FtbTag t, JsonNode obj)
    {
        t.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        t.Name = JsonUtils.RequireString(obj, "name", "'name'");
    }

    private static void LoadArt(FtbArt a, JsonNode obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        a.Url = JsonUtils.RequireString(obj, "url", "'url'");
        a.Type = JsonUtils.RequireString(obj, "type", "'type'");
        a.Width = JsonUtils.RequireInteger(obj, "width", "'width'");
        a.Height = JsonUtils.RequireInteger(obj, "height", "'height'");
        a.Compressed = JsonUtils.RequireBoolean(obj, "compressed", "'compressed'");
        a.Sha1 = JsonUtils.RequireString(obj, "sha1", "'sha1'");
        a.Size = JsonUtils.EnsureInteger(obj["size"]); // optional
        a.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");
    }

    private static void LoadAuthor(FtbAuthor a, JsonNode obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        a.Name = JsonUtils.RequireString(obj, "name", "'name'");
        a.Type = JsonUtils.RequireString(obj, "type", "'type'");
        a.Website = JsonUtils.RequireString(obj, "website", "'website'");
        a.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");
    }

    private static void LoadVersionInfo(FtbVersionInfo v, JsonNode obj)
    {
        v.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        v.Name = JsonUtils.RequireString(obj, "name", "'name'");
        v.Type = JsonUtils.RequireString(obj, "type", "'type'");
        v.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");
        LoadSpecs(v.Specs, JsonUtils.RequireObject(obj, "specs", "'specs'"));
    }

    /// <summary>Port of FTB::loadModpack.</summary>
    public static void LoadModpack(FtbModpack m, JsonNode obj)
    {
        m.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        m.Name = JsonUtils.RequireString(obj, "name", "'name'");
        m.Synopsis = JsonUtils.RequireString(obj, "synopsis", "'synopsis'");
        m.Description = JsonUtils.RequireString(obj, "description", "'description'");
        m.Type = JsonUtils.RequireString(obj, "type", "'type'");
        m.Featured = JsonUtils.RequireBoolean(obj, "featured", "'featured'");
        m.Installs = JsonUtils.RequireInteger(obj, "installs", "'installs'");
        m.Plays = JsonUtils.RequireInteger(obj, "plays", "'plays'");
        m.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");
        m.Refreshed = JsonUtils.EnsureInteger(obj["refreshed"]); // optional

        var artArr = RequireObjectArray(obj, "art");
        foreach (var artObj in artArr)
        {
            var art = new FtbArt();
            LoadArt(art, artObj);
            m.Art.Add(art);
        }

        var authorArr = RequireObjectArray(obj, "authors");
        foreach (var authorObj in authorArr)
        {
            var author = new FtbAuthor();
            LoadAuthor(author, authorObj);
            m.Authors.Add(author);
        }

        var versionArr = RequireObjectArray(obj, "versions");
        foreach (var versionObj in versionArr)
        {
            var version = new FtbVersionInfo();
            LoadVersionInfo(version, versionObj);
            m.Versions.Add(version);
        }

        var tagArr = RequireObjectArray(obj, "tags");
        foreach (var tagObj in tagArr)
        {
            var tag = new FtbTag();
            LoadTag(tag, tagObj);
            m.Tags.Add(tag);
        }

        m.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");
    }

    private static void LoadVersionTarget(FtbVersionTarget a, JsonNode obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        a.Name = JsonUtils.RequireString(obj, "name", "'name'");
        a.Type = JsonUtils.RequireString(obj, "type", "'type'");
        a.Version = JsonUtils.RequireString(obj, "version", "'version'");
        a.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");
    }

    private static void LoadVersionFile(FtbVersionFile a, JsonNode obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        a.Type = JsonUtils.RequireString(obj, "type", "'type'");
        a.Path = JsonUtils.RequireString(obj, "path", "'path'");
        a.Name = JsonUtils.RequireString(obj, "name", "'name'");
        a.Version = JsonUtils.RequireString(obj, "version", "'version'");
        a.Url = JsonUtils.EnsureString(obj["url"]); // optional
        a.Sha1 = JsonUtils.RequireString(obj, "sha1", "'sha1'");
        a.Size = JsonUtils.EnsureInteger(obj["size"]); // optional
        a.ClientOnly = JsonUtils.RequireBoolean(obj, "clientonly", "'clientonly'");
        a.ServerOnly = JsonUtils.RequireBoolean(obj, "serveronly", "'serveronly'");
        a.Optional = JsonUtils.RequireBoolean(obj, "optional", "'optional'");
        a.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");

        var curseforgeObj = obj["curseforge"] as JsonObject; // optional
        a.Curseforge.ProjectId = curseforgeObj is null ? 0 : JsonUtils.EnsureInteger(curseforgeObj["project"]);
        a.Curseforge.FileId = curseforgeObj is null ? 0 : JsonUtils.EnsureInteger(curseforgeObj["file"]);
    }

    /// <summary>Port of FTB::loadVersion.</summary>
    public static void LoadVersion(FtbVersion m, JsonNode obj)
    {
        m.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        m.Parent = JsonUtils.RequireInteger(obj, "parent", "'parent'");
        m.Name = JsonUtils.RequireString(obj, "name", "'name'");
        m.Type = JsonUtils.RequireString(obj, "type", "'type'");
        m.Installs = JsonUtils.RequireInteger(obj, "installs", "'installs'");
        m.Plays = JsonUtils.RequireInteger(obj, "plays", "'plays'");
        m.Updated = JsonUtils.RequireInteger(obj, "updated", "'updated'");
        m.Refreshed = JsonUtils.EnsureInteger(obj["refreshed"]); // optional
        LoadSpecs(m.Specs, JsonUtils.RequireObject(obj, "specs", "'specs'"));

        var targetArr = RequireObjectArray(obj, "targets");
        foreach (var targetObj in targetArr)
        {
            var target = new FtbVersionTarget();
            LoadVersionTarget(target, targetObj);
            m.Targets.Add(target);
        }

        var fileArr = RequireObjectArray(obj, "files");
        foreach (var fileObj in fileArr)
        {
            var file = new FtbVersionFile();
            LoadVersionFile(file, fileObj);
            m.Files.Add(file);
        }
    }
}
