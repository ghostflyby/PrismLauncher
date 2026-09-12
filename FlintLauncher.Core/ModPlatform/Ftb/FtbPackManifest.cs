// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/ftb/FTBPackManifest.{h,cpp}: the modpacks.ch-style JSON models of the
// FTB App API — the pack description (Modpack with art/authors/versions/tags) and the
// version payload (Version with targets[] and files[]).
//
// Deviations: the Json::require* calls throw JsonParseException (the C++ JSONValidationError);
// optional fields keep the QJsonValue coercion defaults (toInt() of a missing value -> 0,
// toString() -> ""); Modpack.safeName is a computed property instead of a parsed field.
using System.Text.Json;
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
    // -------------------------------------------------------- QJsonValue coercions

    private static int ToInt(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
            && value.TryGetValue<double>(out var d) ? (int)d : 0;
    }

    private static string ToString(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
            && value.TryGetValue<string>(out var s) ? s : "";
    }

    // ------------------------------------------------------------------- loaders

    private static void LoadSpecs(FtbSpecs s, JsonObject obj)
    {
        s.Id = JsonUtils.RequireInteger(obj, "id");
        s.Minimum = JsonUtils.RequireInteger(obj, "minimum");
        s.Recommended = JsonUtils.RequireInteger(obj, "recommended");
    }

    private static void LoadTag(FtbTag t, JsonObject obj)
    {
        t.Id = JsonUtils.RequireInteger(obj, "id");
        t.Name = JsonUtils.RequireString(obj, "name");
    }

    private static void LoadArt(FtbArt a, JsonObject obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id");
        a.Url = JsonUtils.RequireString(obj, "url");
        a.Type = JsonUtils.RequireString(obj, "type");
        a.Width = JsonUtils.RequireInteger(obj, "width");
        a.Height = JsonUtils.RequireInteger(obj, "height");
        a.Compressed = JsonUtils.RequireBoolean(obj, "compressed");
        a.Sha1 = JsonUtils.RequireString(obj, "sha1");
        a.Size = ToInt(obj, "size");
        a.Updated = JsonUtils.RequireInteger(obj, "updated");
    }

    private static void LoadAuthor(FtbAuthor a, JsonObject obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id");
        a.Name = JsonUtils.RequireString(obj, "name");
        a.Type = JsonUtils.RequireString(obj, "type");
        a.Website = JsonUtils.RequireString(obj, "website");
        a.Updated = JsonUtils.RequireInteger(obj, "updated");
    }

    private static void LoadVersionInfo(FtbVersionInfo v, JsonObject obj)
    {
        v.Id = JsonUtils.RequireInteger(obj, "id");
        v.Name = JsonUtils.RequireString(obj, "name");
        v.Type = JsonUtils.RequireString(obj, "type");
        v.Updated = JsonUtils.RequireInteger(obj, "updated");
        LoadSpecs(v.Specs, RequireObject(JsonUtils.RequireObject(obj, "specs")));
    }

    /// <summary>Port of FTB::loadModpack.</summary>
    public static void LoadModpack(FtbModpack m, JsonObject obj)
    {
        m.Id = JsonUtils.RequireInteger(obj, "id");
        m.Name = JsonUtils.RequireString(obj, "name");
        m.Synopsis = JsonUtils.RequireString(obj, "synopsis");
        m.Description = JsonUtils.RequireString(obj, "description");
        m.Type = JsonUtils.RequireString(obj, "type");
        m.Featured = JsonUtils.RequireBoolean(obj, "featured");
        m.Installs = JsonUtils.RequireInteger(obj, "installs");
        m.Plays = JsonUtils.RequireInteger(obj, "plays");
        m.Updated = JsonUtils.RequireInteger(obj, "updated");
        m.Refreshed = ToInt(obj, "refreshed");

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

        m.Updated = JsonUtils.RequireInteger(obj, "updated");
    }

    private static void LoadVersionTarget(FtbVersionTarget a, JsonObject obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id");
        a.Name = JsonUtils.RequireString(obj, "name");
        a.Type = JsonUtils.RequireString(obj, "type");
        a.Version = JsonUtils.RequireString(obj, "version");
        a.Updated = JsonUtils.RequireInteger(obj, "updated");
    }

    private static void LoadVersionFile(FtbVersionFile a, JsonObject obj)
    {
        a.Id = JsonUtils.RequireInteger(obj, "id");
        a.Type = JsonUtils.RequireString(obj, "type");
        a.Path = JsonUtils.RequireString(obj, "path");
        a.Name = JsonUtils.RequireString(obj, "name");
        a.Version = JsonUtils.RequireString(obj, "version");
        a.Url = ToString(obj, "url"); // optional
        a.Sha1 = JsonUtils.RequireString(obj, "sha1");
        a.Size = ToInt(obj, "size");
        a.ClientOnly = JsonUtils.RequireBoolean(obj, "clientonly");
        a.ServerOnly = JsonUtils.RequireBoolean(obj, "serveronly");
        a.Optional = JsonUtils.RequireBoolean(obj, "optional");
        a.Updated = JsonUtils.RequireInteger(obj, "updated");

        var curseforgeObj = obj.TryGetPropertyValue("curseforge", out var cfNode)
            && cfNode is JsonObject cf ? cf : null; // optional
        a.Curseforge.ProjectId = curseforgeObj is null ? 0 : ToInt(curseforgeObj, "project");
        a.Curseforge.FileId = curseforgeObj is null ? 0 : ToInt(curseforgeObj, "file");
    }

    /// <summary>Port of FTB::loadVersion.</summary>
    public static void LoadVersion(FtbVersion m, JsonObject obj)
    {
        m.Id = JsonUtils.RequireInteger(obj, "id");
        m.Parent = JsonUtils.RequireInteger(obj, "parent");
        m.Name = JsonUtils.RequireString(obj, "name");
        m.Type = JsonUtils.RequireString(obj, "type");
        m.Installs = JsonUtils.RequireInteger(obj, "installs");
        m.Plays = JsonUtils.RequireInteger(obj, "plays");
        m.Updated = JsonUtils.RequireInteger(obj, "updated");
        m.Refreshed = ToInt(obj, "refreshed");
        LoadSpecs(m.Specs, RequireObject(JsonUtils.RequireObject(obj, "specs")));

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

    // ------------------------------------------------- QJsonObject-style helpers

    private static JsonObject RequireObject(JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText()) as JsonObject
            ?? throw new JsonParseException("Value is not an object");
    }

    private static List<JsonObject> RequireObjectArray(JsonObject obj, string key)
    {
        var result = new List<JsonObject>();
        foreach (var item in JsonUtils.RequireArray(obj, key).EnumerateArray())
        {
            result.Add(RequireObject(item));
        }
        return result;
    }
}
