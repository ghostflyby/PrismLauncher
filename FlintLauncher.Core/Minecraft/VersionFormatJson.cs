// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Typed wire probes + mappers for the Mojang/OneSix version format, both directions. The
// format classes keep their public node-based READ entry points (the tests and callers pin
// them); each entry deserializes the probe for the whole document once and maps it onto the
// existing VersionFile/Library/Rule/JavaMetadata domain objects. The WRITE side maps the
// domain objects back onto the *OutJson records and serializes them through the same
// context — the byte-tested Qt tail (sorted keys, 4-space indent) is retired
// (docs/JSON-MIGRATION.md, the Qt write-tail batch: patches and mmc-pack have no external
// write consumer; the values are the contract).
//
// Failure behavior: the former Require* reads become document-level JsonException
// failures with the collapsed kind texts ("'sha1' is not a string"); per-field quirks with
// external meaning are preserved (library 'name' missing keeps the upstream typo message,
// assetIndex-absent synthesizes from 'assets', minimumLauncherVersion > 18 warns, the
// removed-feature members add problems, uid keeps the fileId fallback and the regex check).

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Meta;
using JdkFind;

namespace FlintLauncher.Core.Minecraft;

internal sealed record MojangDownloadInfoJson(
    string? Path,
    string? Sha1,
    string? Url,
    int? Size);

internal sealed record MojangAssetIndexJson(
    string? Path,
    string? Sha1,
    string? Url,
    int? Size,
    int? TotalSize,
    string? Id);

internal sealed record MojangLibraryDownloadsJson(
    MojangDownloadInfoJson? Artifact,
    Dictionary<string, MojangDownloadInfoJson>? Classifiers);

internal sealed record LibraryExtractJson(
    List<string>? Exclude);

internal sealed record RuleJson(
    JsonElement? Action,
    JsonElement? Os);

internal sealed record LibraryJson
{
    public string? Name { get; init; }
    public string? Url { get; init; }
    public LibraryExtractJson? Extract { get; init; }

    /// <summary>raw elements: non-string native classifiers fall back to "" (QJsonValue::toString default), like the former EnsureString read.</summary>
    public Dictionary<string, JsonElement>? Natives { get; init; }

    public List<RuleJson>? Rules { get; init; }

    public MojangLibraryDownloadsJson? Downloads { get; init; }

    // OneSix MMC extensions (+ the +agents argument member)
    [JsonPropertyName("MMC-hint")]
    public string? MmcHint { get; init; }

    [JsonPropertyName("MMC-absulute_url")]
    public string? MmcAbsuluteUrl { get; init; }

    [JsonPropertyName("MMC-absoluteUrl")]
    public string? MmcAbsoluteUrl { get; init; }

    [JsonPropertyName("MMC-filename")]
    public string? MmcFilename { get; init; }

    [JsonPropertyName("MMC-displayname")]
    public string? MmcDisplayname { get; init; }

    public string? Argument { get; init; }
}

/// <summary>The DEPRECATED '+jarMods' entry shape.</summary>
internal sealed record PlusJarModJson(
    string? Name,
    string? OriginalName);

internal sealed record JavaChecksumJson(
    string? Hash,
    string? Type);

internal sealed record JavaVersionJson(
    string? Name,
    int? Major,
    int? Minor,
    int? Security,
    int? Build);

internal sealed record JavaRuntimeJson(
    string? Name,
    string? Vendor,
    string? Url,
    string? ReleaseTime,
    string? DownloadType,
    string? PackageType,
    // all-caps "OS" on the wire — camelCase would produce "runtimeOs"
    [property: JsonPropertyName("runtimeOS")]
    string? RuntimeOs,
    JavaChecksumJson? Checksum,
    JavaVersionJson? Version);

internal sealed record VersionFileJson
{
    // OneSix
    /// <summary>The unbound formatVersion member (any token binds; ParseFormatVersion
    /// judges the raw number token).</summary>
    public JsonElement? FormatVersion { get; init; }

    public int? Order { get; init; }
    public string? Name { get; init; }
    public string? Uid { get; init; }
    public string? FileId { get; init; }
    public string? Version { get; init; }
    public string? McVersion { get; init; }
    public string? AppletClass { get; init; }

    [JsonPropertyName("+tweakers")]
    public List<string>? AddTweakers { get; init; }

    [JsonPropertyName("+traits")]
    public List<string>? AddTraits { get; init; }

    [JsonPropertyName("+jvmArgs")]
    public List<string>? AddJvmArgs { get; init; }

    public List<LibraryJson>? JarMods { get; init; }

    [JsonPropertyName("+jarMods")]
    public List<PlusJarModJson>? PlusJarMods { get; init; }

    public List<LibraryJson>? Mods { get; init; }
    public List<LibraryJson>? Libraries { get; init; }

    [JsonPropertyName("+libraries")]
    public List<LibraryJson>? PlusLibraries { get; init; }

    public List<LibraryJson>? MavenFiles { get; init; }

    [JsonPropertyName("+agents")]
    public List<LibraryJson>? Agents { get; init; }

    public LibraryJson? MainJar { get; init; }
    public bool? Volatile { get; init; }

    public List<JavaRuntimeJson>? Runtimes { get; init; }

    // the shared requires wire (MetaRequireJson — the meta documents and the mmc-pack
    // components flow through the same records)
    public List<MetaRequireJson?>? Requires { get; init; }

    public List<MetaRequireJson?>? Conflicts { get; init; }

    // Mojang
    public string? Id { get; init; }
    public string? MainClass { get; init; }
    public string? MinecraftArguments { get; init; }
    public string? Type { get; init; }
    public string? Assets { get; init; }
    public MojangAssetIndexJson? AssetIndex { get; init; }
    public string? ReleaseTime { get; init; }
    public string? Time { get; init; }
    public int? MinimumLauncherVersion { get; init; }
    public List<int>? CompatibleJavaMajors { get; init; }
    public string? CompatibleJavaName { get; init; }

    public Dictionary<string, MojangDownloadInfoJson>? Downloads { get; init; }

    // removed features — only the PRESENCE matters (each adds a problem)
    [JsonPropertyName("tweakers")]
    public JsonElement? RemovedTweakers { get; init; }

    [JsonPropertyName("-libraries")]
    public JsonElement? RemovedLibraries { get; init; }

    [JsonPropertyName("-tweakers")]
    public JsonElement? RemovedTweakersDash { get; init; }

    [JsonPropertyName("-minecraftArguments")]
    public JsonElement? RemovedMinecraftArguments { get; init; }

    [JsonPropertyName("+minecraftArguments")]
    public JsonElement? RemovedPlusMinecraftArguments { get; init; }
}

// ---------------------------------------------------------------------------
// the WRITE records (model → wire). Separate from the read probes: the write side is
// strongly typed (non-null members are the always-written ones), a null member is simply
// not written (WhenWritingNull), matching the former conditional JsonObject inserts.

internal sealed record DownloadInfoOutJson
{
    public string? Path { get; init; }

    public string Sha1 { get; init; } = "";

    public int Size { get; init; }

    public string Url { get; init; } = "";
}

internal sealed record AssetIndexOutJson
{
    public string? Path { get; init; }

    public string Sha1 { get; init; } = "";

    public int Size { get; init; }

    public string Url { get; init; } = "";

    public int TotalSize { get; init; }

    public string Id { get; init; } = "";
}

internal sealed record LibDownloadsOutJson
{
    public DownloadInfoOutJson? Artifact { get; init; }

    public Dictionary<string, DownloadInfoOutJson>? Classifiers { get; init; }
}

internal sealed record RuleOsOutJson
{
    public string Name { get; init; } = "";

    public string? Version { get; init; }
}

internal sealed record RuleOutJson
{
    /// <summary>"allow"/"disallow"; a Defer rule writes nothing (the C++ ToJson).</summary>
    public string? Action { get; init; }

    public RuleOsOutJson? Os { get; init; }
}

internal sealed record LibraryOutJson
{
    public string Name { get; init; } = "";

    public string? Url { get; init; }

    public LibraryExtractJson? Extract { get; init; }

    public Dictionary<string, string>? Natives { get; init; }

    public List<RuleOutJson>? Rules { get; init; }

    public LibDownloadsOutJson? Downloads { get; init; }

    // OneSix MMC extensions (+ the +agents argument member)
    [JsonPropertyName("MMC-hint")]
    public string? MmcHint { get; init; }

    [JsonPropertyName("MMC-absoluteUrl")]
    public string? MmcAbsoluteUrl { get; init; }

    [JsonPropertyName("MMC-filename")]
    public string? MmcFilename { get; init; }

    [JsonPropertyName("MMC-displayname")]
    public string? MmcDisplayname { get; init; }

    public string? Argument { get; init; }
}

/// <summary>
/// The version file write wire — a superset of the Mojang and OneSix member sets;
/// the Mojang entry leaves the OneSix members null (unwritten) and vice versa. NOTE: the
/// requires/conflicts members are deliberately absent — the former node write's inverted
/// emptiness check made SerializeRequires a no-op in every case, so they were never written.
/// </summary>
internal sealed record VersionFileOutJson
{
    // OneSix
    public int? Order { get; init; }

    public string? Name { get; init; }

    public string? Uid { get; init; }

    public string? Version { get; init; }

    [JsonPropertyName("formatVersion")]
    public int? FormatVersion { get; init; }

    public string? AppletClass { get; init; }

    [JsonPropertyName("+tweakers")]
    public List<string>? AddTweakers { get; init; }

    [JsonPropertyName("+traits")]
    public List<string>? AddTraits { get; init; }

    [JsonPropertyName("+jvmArgs")]
    public List<string>? AddJvmArgs { get; init; }

    [JsonPropertyName("+agents")]
    public List<LibraryOutJson>? Agents { get; init; }

    public List<LibraryOutJson>? JarMods { get; init; }

    public List<LibraryOutJson>? Mods { get; init; }

    public List<LibraryOutJson>? Libraries { get; init; }

    public List<LibraryOutJson>? MavenFiles { get; init; }

    public LibraryOutJson? MainJar { get; init; }

    public bool? Volatile { get; init; }

    // Mojang
    public string? Id { get; init; }

    public string? MainClass { get; init; }

    public string? MinecraftArguments { get; init; }

    public string? Type { get; init; }

    public string? ReleaseTime { get; init; }

    public string? Time { get; init; }

    public int? MinimumLauncherVersion { get; init; }

    public string? Assets { get; init; }

    public AssetIndexOutJson? AssetIndex { get; init; }

    public Dictionary<string, DownloadInfoOutJson>? Downloads { get; init; }

    public List<int>? CompatibleJavaMajors { get; init; }

    public string? CompatibleJavaName { get; init; }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(VersionFileJson))]
[JsonSerializable(typeof(LibraryJson))]
[JsonSerializable(typeof(RuleJson))]
[JsonSerializable(typeof(VersionFileOutJson))]
[JsonSerializable(typeof(LibraryOutJson))]
// (the remaining probe shapes are reachable through the roots; only standalone
// deserialize/serialize roots need explicit registration)
internal sealed partial class VersionFormatJsonContext : JsonSerializerContext;

/// <summary>The mappers from the version-format probes onto the domain objects. All shared
/// helpers throw the collapsed JsonException kind texts — a malformed document fails
/// whole (the reasonable failure behavior for format files; the consumers surface the
/// message and the file can be fixed or refetched).</summary>
internal static class VersionFormatMapper
{
    public static JsonException MissingInt(string key) => new($"'{key}' is not an integer");

    public static JsonException MissingString(string key) => new($"'{key}' is not a string");

    public static JsonException MissingObject(string key) => new($"'{key}' is not an object");

    public static string Require(string? value, string key) => value ?? throw MissingString(key);

    public static int Require(int? value, string key) => value ?? throw MissingInt(key);

    public static JsonException MissingArray(string key) => new($"'{key}' is not an array");

    /// <summary>Collection/dictionary elements are not null-checked by STJ (even with
    /// RespectNullableAnnotations) — a JSON null element is the former "Value is not an
    /// object" binding failure.</summary>
    public static T RequireElement<T>(T? element) where T : class =>
        element ?? throw new JsonException("Value is not an object");

    // ---------------------------------------------------------------- download info

    public static MojangDownloadInfo MapDownloadInfo(MojangDownloadInfoJson? dto)
    {
        if (dto is null)
        {
            throw new JsonException("Value is not an object");
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
            throw new JsonException("Value is not an object");
        }

        if (dto.Name is null)
        {
            // the upstream typo of the missing space is preserved
            throw new JsonException(filename + "contains a library that doesn't have a 'name' field");
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
            throw new JsonException("Value is not an object");
        }

        if (dto.Name is null)
        {
            throw new JsonException(filename + "contains a jarmod that doesn't have a 'name' field");
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
            Name = dto.Name ?? "",
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
            meta = meta with
            {
                Version = JvmVersion.Parse(JavaMetadata.ComposeVersion(
                    version.Major ?? 0,
                    version.Minor ?? 0,
                    version.Security ?? 0,
                    version.Build ?? 0))
            };
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
