// SPDX-License-Identifier: GPL-3.0-only
// Typed wire probes + mappers for the Mojang/OneSix version format read side. The format
// classes keep their public node-based entry points (the tests and callers pin them); each
// entry deserializes the probe for the whole document once and maps it onto the existing
// VersionFile/Library/Rule/JavaMetadata domain objects. The write side stays node-based —
// it is the byte-tested Qt tail (sorted keys, 4-space indent).
//
// Failure behavior: the former Require* reads become document-level JsonParseException
// failures with the collapsed kind texts ("'sha1' is not a string"); per-field quirks with
// external meaning are preserved (library 'name' missing keeps the upstream typo message,
// assetIndex-absent synthesizes from 'assets', minimumLauncherVersion > 18 warns, the
// removed-feature members add problems, uid keeps the fileId fallback and the regex check).

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Meta;

namespace FlintLauncher.Core.Minecraft;

internal sealed record MojangDownloadInfoJson(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("sha1")] string? Sha1,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("size")] int? Size);

internal sealed record MojangAssetIndexJson(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("sha1")] string? Sha1,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("size")] int? Size,
    [property: JsonPropertyName("totalSize")] int? TotalSize,
    [property: JsonPropertyName("id")] string? Id);

internal sealed record MojangLibraryDownloadsJson(
    [property: JsonPropertyName("artifact")] MojangDownloadInfoJson? Artifact,
    [property: JsonPropertyName("classifiers")] Dictionary<string, MojangDownloadInfoJson>? Classifiers);

internal sealed record LibraryExtractJson(
    [property: JsonPropertyName("exclude")] List<string>? Exclude);

internal sealed record RuleJson(
    [property: JsonPropertyName("action")] JsonElement? Action,
    [property: JsonPropertyName("os")] JsonElement? Os);

internal sealed record LibraryJson
{
    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }

    [JsonPropertyName("extract")] public LibraryExtractJson? Extract { get; init; }

    /// <summary>raw elements: non-string native classifiers fall back to "" (QJsonValue::toString default), like the former EnsureString read.</summary>
    [JsonPropertyName("natives")] public Dictionary<string, JsonElement>? Natives { get; init; }

    [JsonPropertyName("rules")] public List<RuleJson>? Rules { get; init; }

    [JsonPropertyName("downloads")] public MojangLibraryDownloadsJson? Downloads { get; init; }

    // OneSix MMC extensions (+ the +agents argument member)
    [JsonPropertyName("MMC-hint")] public string? MmcHint { get; init; }

    [JsonPropertyName("MMC-absulute_url")] public string? MmcAbsuluteUrl { get; init; }

    [JsonPropertyName("MMC-absoluteUrl")] public string? MmcAbsoluteUrl { get; init; }

    [JsonPropertyName("MMC-filename")] public string? MmcFilename { get; init; }

    [JsonPropertyName("MMC-displayname")] public string? MmcDisplayname { get; init; }

    [JsonPropertyName("argument")] public string? Argument { get; init; }
}

/// <summary>The DEPRECATED '+jarMods' entry shape.</summary>
internal sealed record PlusJarModJson(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("originalName")] string? OriginalName);

internal sealed record JavaChecksumJson(
    [property: JsonPropertyName("hash")] string? Hash,
    [property: JsonPropertyName("type")] string? Type);

internal sealed record JavaVersionJson(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("major")] int? Major,
    [property: JsonPropertyName("minor")] int? Minor,
    [property: JsonPropertyName("security")] int? Security,
    [property: JsonPropertyName("build")] int? Build);

internal sealed record JavaRuntimeJson(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("releaseTime")] string? ReleaseTime,
    [property: JsonPropertyName("downloadType")] string? DownloadType,
    [property: JsonPropertyName("packageType")] string? PackageType,
    [property: JsonPropertyName("runtimeOS")] string? RuntimeOs,
    [property: JsonPropertyName("checksum")] JavaChecksumJson? Checksum,
    [property: JsonPropertyName("version")] JavaVersionJson? Version);

internal sealed record VersionFileJson
{
    // OneSix
    [JsonPropertyName("order")] public int? Order { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("uid")] public string? Uid { get; init; }

    [JsonPropertyName("fileId")] public string? FileId { get; init; }

    [JsonPropertyName("version")] public string? Version { get; init; }

    [JsonPropertyName("mcVersion")] public string? McVersion { get; init; }

    [JsonPropertyName("appletClass")] public string? AppletClass { get; init; }

    [JsonPropertyName("+tweakers")] public List<string>? AddTweakers { get; init; }

    [JsonPropertyName("+traits")] public List<string>? AddTraits { get; init; }

    [JsonPropertyName("+jvmArgs")] public List<string>? AddJvmArgs { get; init; }

    [JsonPropertyName("jarMods")] public List<LibraryJson>? JarMods { get; init; }

    [JsonPropertyName("+jarMods")] public List<PlusJarModJson>? PlusJarMods { get; init; }

    [JsonPropertyName("mods")] public List<LibraryJson>? Mods { get; init; }

    [JsonPropertyName("libraries")] public List<LibraryJson>? Libraries { get; init; }

    [JsonPropertyName("+libraries")] public List<LibraryJson>? PlusLibraries { get; init; }

    [JsonPropertyName("mavenFiles")] public List<LibraryJson>? MavenFiles { get; init; }

    [JsonPropertyName("+agents")] public List<LibraryJson>? Agents { get; init; }

    [JsonPropertyName("mainJar")] public LibraryJson? MainJar { get; init; }

    [JsonPropertyName("volatile")] public bool? Volatile { get; init; }

    [JsonPropertyName("runtimes")] public List<JavaRuntimeJson>? Runtimes { get; init; }

    // Mojang
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("mainClass")] public string? MainClass { get; init; }

    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; init; }

    [JsonPropertyName("type")] public string? Type { get; init; }

    [JsonPropertyName("assets")] public string? Assets { get; init; }

    [JsonPropertyName("assetIndex")] public MojangAssetIndexJson? AssetIndex { get; init; }

    [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; init; }

    [JsonPropertyName("time")] public string? Time { get; init; }

    [JsonPropertyName("minimumLauncherVersion")] public int? MinimumLauncherVersion { get; init; }

    [JsonPropertyName("compatibleJavaMajors")] public List<int>? CompatibleJavaMajors { get; init; }

    [JsonPropertyName("compatibleJavaName")] public string? CompatibleJavaName { get; init; }

    [JsonPropertyName("downloads")] public Dictionary<string, MojangDownloadInfoJson>? Downloads { get; init; }

    // removed features — only the PRESENCE matters (each adds a problem)
    [JsonPropertyName("tweakers")] public JsonElement? RemovedTweakers { get; init; }

    [JsonPropertyName("-libraries")] public JsonElement? RemovedLibraries { get; init; }

    [JsonPropertyName("-tweakers")] public JsonElement? RemovedTweakersDash { get; init; }

    [JsonPropertyName("-minecraftArguments")] public JsonElement? RemovedMinecraftArguments { get; init; }

    [JsonPropertyName("+minecraftArguments")] public JsonElement? RemovedPlusMinecraftArguments { get; init; }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true)]
[JsonSerializable(typeof(VersionFileJson))]
[JsonSerializable(typeof(LibraryJson))]
[JsonSerializable(typeof(RuleJson))]
// (the remaining probe shapes are reachable through the two roots; only standalone
// deserialize roots need explicit registration)
internal sealed partial class VersionFormatJsonContext : JsonSerializerContext;

/// <summary>The mappers from the version-format probes onto the domain objects. All shared
/// helpers throw the collapsed JsonParseException kind texts — a malformed document fails
/// whole (the reasonable failure behavior for format files; the consumers surface the
/// message and the file can be fixed or refetched).</summary>
internal static class VersionFormatMapper
{
    public static JsonParseException MissingInt(string key) => new($"'{key}' is not an integer");

    public static JsonParseException MissingString(string key) => new($"'{key}' is not a string");

    public static JsonParseException MissingObject(string key) => new($"'{key}' is not an object");

    public static string Require(string? value, string key) => value ?? throw MissingString(key);

    public static int Require(int? value, string key) => value ?? throw MissingInt(key);

    public static JsonParseException MissingArray(string key) => new($"'{key}' is not an array");

    /// <summary>Collection/dictionary elements are not null-checked by STJ (even with
    /// RespectNullableAnnotations) — a JSON null element is the former "Value is not an
    /// object" binding failure.</summary>
    public static T RequireElement<T>(T? element) where T : class =>
        element ?? throw new JsonParseException("Value is not an object");

    // ---------------------------------------------------------------- download info

    public static MojangDownloadInfo MapDownloadInfo(MojangDownloadInfoJson? dto)
    {
        if (dto is null)
        {
            throw new JsonParseException("Value is not an object");
        }

        var info = new MojangDownloadInfo();
        if (dto.Path is not null)
        {
            info.Path = dto.Path;
        }

        info.Sha1 = Require(dto.Sha1, "sha1");
        info.Url = Require(dto.Url, "url");
        info.Size = Require(dto.Size, "size");
        return info;
    }

    public static MojangAssetIndexInfo MapAssetIndex(MojangAssetIndexJson dto)
    {
        var info = new MojangAssetIndexInfo
        {
            TotalSize = Require(dto.TotalSize, "totalSize"),
            Id = Require(dto.Id, "id"),
        };
        if (dto.Path is not null)
        {
            info.Path = dto.Path;
        }

        info.Sha1 = Require(dto.Sha1, "sha1");
        info.Url = Require(dto.Url, "url");
        info.Size = Require(dto.Size, "size");
        return info;
    }

    public static MojangLibraryDownloadInfo MapLibDownloads(MojangLibraryDownloadsJson dto)
    {
        var info = new MojangLibraryDownloadInfo();
        if (dto.Artifact is { } artifact)
        {
            info.Artifact = MapDownloadInfo(artifact);
        }

        if (dto.Classifiers is { } classifiers)
        {
            foreach (var (classifier, download) in classifiers)
            {
                info.Classifiers[classifier] = MapDownloadInfo(download);
            }
        }

        return info;
    }

    // ------------------------------------------------------------------------ rule

    public static Rule MapRule(RuleJson dto)
    {
        var action = Rule.Action.Defer;
        if (dto.Action is { ValueKind: JsonValueKind.String } rawAction)
        {
            action = rawAction.GetString() switch
            {
                "allow" => Rule.Action.Allow,
                "disallow" => Rule.Action.Disallow,
                _ => Rule.Action.Defer,
            };
        }

        // an os member only counts when it is an object holding a string name; the version
        // member is retained even though upstream does not act on it
        string? osName = null;
        var osVersion = "";
        if (dto.Os is { ValueKind: JsonValueKind.Object } os)
        {
            if (os.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                osName = name.GetString() ?? "";
                osVersion = os.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString() ?? ""
                    : "";
            }
        }

        return Rule.FromWire(action, osName, osVersion);
    }

    // --------------------------------------------------------------------- library

    public static Library MapLibrary(LibraryJson? dto, ProblemContainer problems, string filename)
    {
        if (dto is null)
        {
            throw new JsonParseException("Value is not an object");
        }

        if (dto.Name is null)
        {
            // the upstream typo of the missing space is preserved
            throw new JsonParseException(filename + "contains a library that doesn't have a 'name' field");
        }

        var outLib = new Library();
        var rawName = dto.Name;
        outLib.Name = new GradleSpecifier(rawName);
        if (!outLib.Name.IsValid)
        {
            problems.AddProblem(ProblemSeverity.Error, $"Library {rawName} name is broken and cannot be processed.");
        }

        if (dto.Url is not null)
        {
            outLib.RepositoryUrl = dto.Url;
        }

        if (dto.Extract is { } extract)
        {
            outLib.HasExcludes = true;
            if (extract.Exclude is { } excludes)
            {
                outLib.ExtractExcludes.AddRange(excludes);
            }
            else
            {
                throw MissingArray("exclude");
            }
        }

        if (dto.Natives is { } natives)
        {
            foreach (var (os, classifier) in natives)
            {
                // FIXME upstream: Skip unknown platforms — non-string classifiers fall
                // back to "" like QJsonValue::toString(default)
                outLib.NativeClassifiers[os] =
                    classifier.ValueKind == JsonValueKind.String ? classifier.GetString() ?? "" : "";
            }
        }

        if (dto.Rules is { } rules)
        {
            outLib.ApplyRules = true;
            foreach (var rule in rules)
            {
                outLib.Rules.Add(MapRule(RequireElement(rule)));
            }
        }

        if (dto.Downloads is not null)
        {
            outLib.MojangDownloads = MapLibDownloads(dto.Downloads);
        }

        return outLib;
    }

    /// <summary>The OneSix MMC extension keys over the Mojang-mapped library.</summary>
    public static void MapMmcLibrary(LibraryJson dto, Library outLib)
    {
        if (dto.MmcHint is not null)
        {
            outLib.Hint = dto.MmcHint;
        }

        if (dto.MmcAbsuluteUrl is not null)
        {
            outLib.AbsoluteUrl = dto.MmcAbsuluteUrl;
        }

        if (dto.MmcAbsoluteUrl is not null)
        {
            outLib.AbsoluteUrl = dto.MmcAbsoluteUrl;
        }

        if (dto.MmcFilename is not null)
        {
            outLib.FilenameOverride = dto.MmcFilename;
        }

        if (dto.MmcDisplayname is not null)
        {
            outLib.DisplayNameValue = dto.MmcDisplayname;
        }
    }

    public static PlusJarModParts MapPlusJarMod(PlusJarModJson? dto, string filename, string originalName)
    {
        if (dto is null)
        {
            throw new JsonParseException("Value is not an object");
        }

        if (dto.Name is null)
        {
            throw new JsonParseException(filename + "contains a jarmod that doesn't have a 'name' field");
        }

        // it needs to be local, it is stored in the instance jarmods folder; the display
        // name is the original jar mod filename before it got renamed at addition
        var displayName = dto.OriginalName ?? "";
        if (displayName.Length == 0)
        {
            displayName = originalName.Replace(" (jar mod)", "");
        }

        return new PlusJarModParts(FilenameOverride: dto.Name, DisplayName: displayName);
    }

    public readonly record struct PlusJarModParts(string FilenameOverride, string DisplayName);

    // --------------------------------------------------------------- java runtimes

    public static JavaMetadata MapJavaRuntime(JavaRuntimeJson dto)
    {
        var meta = new JavaMetadata
        {
            name = dto.Name ?? "",
            Vendor = dto.Vendor ?? "",
            Url = dto.Url ?? "",
            ReleaseTime = ParseUtils.TimeFromS3Time(dto.ReleaseTime ?? ""),
            DownloadType = JavaMetadata.ParseDownloadType(dto.DownloadType ?? ""),
            PackageType = dto.PackageType ?? "",
            RuntimeOs = dto.RuntimeOs ?? "unknown",
        };

        if (dto.Checksum is { } checksum)
        {
            meta.ChecksumHash = checksum.Hash ?? "";
            meta.ChecksumType = checksum.Type ?? "";
        }

        if (dto.Version is { } version)
        {
            meta.Version = new JavaVersion(
                version.Major ?? 0,
                version.Minor ?? 0,
                version.Security ?? 0,
                version.Build ?? 0,
                version.Name ?? "");
        }

        return meta;
    }

    // ---------------------------------------------------------------- version file

    /// <summary>The Mojang member half (does not include libraries); members absent from the
    /// document keep their current values, like the former ReadString(key, current) reads.</summary>
    public static void MapVersionProperties(VersionFileJson dto, VersionFile outInfo)
    {
        outInfo.MinecraftVersion = dto.Id ?? outInfo.MinecraftVersion;
        outInfo.MainClass = dto.MainClass ?? outInfo.MainClass;
        outInfo.MinecraftArguments = dto.MinecraftArguments ?? outInfo.MinecraftArguments;
        outInfo.Type = dto.Type ?? outInfo.Type;

        outInfo.Assets = dto.Assets ?? outInfo.Assets;
        if (dto.AssetIndex is { } assetIndex)
        {
            outInfo.MojangAssetIndex = MapAssetIndex(assetIndex);
        }
        else if (outInfo.Assets.Length != 0)
        {
            outInfo.MojangAssetIndex = new MojangAssetIndexInfo(outInfo.Assets);
        }

        outInfo.ReleaseTime = ParseUtils.TimeFromS3Time(dto.ReleaseTime ?? "");
        outInfo.UpdateTime = ParseUtils.TimeFromS3Time(dto.Time ?? "");

        if (dto.MinimumLauncherVersion is { } minimumLauncherVersion)
        {
            outInfo.MinimumLauncherVersion = minimumLauncherVersion;
            if (minimumLauncherVersion > MojangVersionFormat.CurrentMinimumLauncherVersion)
            {
                outInfo.AddProblem(ProblemSeverity.Warning,
                    $"The 'minimumLauncherVersion' value of this version ({minimumLauncherVersion}) is higher than " +
                    $"supported by {FlintConfig.Default.LauncherDisplayName} ({MojangVersionFormat.CurrentMinimumLauncherVersion}). It might not work properly!");
            }
        }

        if (dto.CompatibleJavaMajors is { } majors)
        {
            outInfo.CompatibleJavaMajors.AddRange(majors);
        }

        if (dto.CompatibleJavaName is not null)
        {
            outInfo.CompatibleJavaName = dto.CompatibleJavaName;
        }

        if (dto.Downloads is { } downloads)
        {
            foreach (var (classifier, download) in downloads)
            {
                outInfo.MojangDownloads[classifier] = MapDownloadInfo(download);
            }
        }
    }
}
