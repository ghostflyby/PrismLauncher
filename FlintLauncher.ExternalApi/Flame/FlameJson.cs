// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Strongly-typed wire models for the CurseForge v1 API, with the source-generated
// serializer context that owns them. Every member is camel-conforming on the wire
// (id/name/slug/links/logo/authors/classId/gameVersions/fileDate/...), so the context-wide
// naming policy carries it with zero explicit annotations; the members this codebase
// consumes are nullable where CurseForge may omit them (the loaders' Require reads enforce
// the ones a document must carry), and only the modeled members are read — the serializer
// ignores the rest.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlintLauncher.ExternalApi.Flame;

// ------------------------------------------------------------------------- request bodies

/// <summary>POST /fingerprints body — the id lists are strings of decimal numbers, the C++
/// compatibility quirk kept verbatim.</summary>
public sealed record FlameFingerprintsRequest(List<string> Fingerprints);

/// <summary>POST /mods body (same decimal-string quirk).</summary>
public sealed record FlameModsRequest(List<string> ModIds);

/// <summary>POST /mods/files body (same decimal-string quirk).</summary>
public sealed record FlameFilesRequest(List<string> FileIds);

// ---------------------------------------------------------------- project & file shapes

public sealed record FlameLinks(
    string? WebsiteUrl,
    string? IssuesUrl,
    string? SourceUrl,
    string? WikiUrl);

public sealed record FlameLogo(
    string? Title,
    string? ThumbnailUrl,
    string? Url);

public sealed record FlameAuthor(
    string? Name,
    string? Url);

/// <summary>A CurseForge project (the /mods shapes and the search hits alike; the hits add
/// downloadCount).</summary>
public sealed record FlameProject(
    int? Id,
    string? Name,
    string? Slug,
    FlameLinks? Links,
    string? Summary,
    FlameLogo? Logo,
    List<FlameAuthor>? Authors,
    int? ClassId,
    long? DownloadCount);

public sealed record FlameHash(
    int? Algo,
    string? Value);

public sealed record FlameDependency(
    int? ModId,
    int? RelationType);

/// <summary>A CurseForge file (the /mods/.../files shapes). GameVersions stays the raw JSON
/// element: the members mix MC versions with loader/side strings, and the loader reads them
/// in place.</summary>
public sealed record FlameModFile(
    JsonElement? GameVersions,
    int? ModId,
    int? Id,
    string? FileDate,
    string? DisplayName,
    string? DownloadUrl,
    string? FileName,
    int? ReleaseType,
    List<FlameHash>? Hashes,
    List<FlameDependency>? Dependencies,
    bool? IsAvailable,
    long? FileFingerprint);

// ------------------------------------------------------------------------ envelopes

/// <summary>The {"data": "&lt;string&gt;"} envelope of the changelog/description endpoints.</summary>
public sealed record FlameStringEnvelope(
    string? Data);

/// <summary>The {data: [...]} envelope of the files list response.</summary>
public sealed record FlameFileList(
    List<FlameModFile>? Data);

/// <summary>The {data: {...}} envelope of the single-file response.</summary>
public sealed record FlameFileEnvelope(
    FlameModFile? Data);

/// <summary>The {data: [...]} envelope of the projects list and search responses. Elements
/// stay nullable: a JSON null project is tolerated by the readers, not silently re-typed.</summary>
public sealed record FlameProjectList(
    List<FlameProject?>? Data);

/// <summary>The {data: {...}} envelope of the single-project response.</summary>
public sealed record FlameProjectEnvelope(
    FlameProject? Data);

/// <summary>The /categories response.</summary>
public sealed record FlameCategoryList(
    List<FlameCategory>? Data);

public sealed record FlameCategory(
    int? Id,
    string? Name,
    string? Icon);

/// <summary>The fingerprint-match response.</summary>
public sealed record FlameFingerprintResponse(
    FlameFingerprintData? Data);

public sealed record FlameFingerprintData(
    List<FlameFingerprintMatch>? ExactMatches);

/// <summary>One fingerprint-search match; the fingerprint itself is the FILE object's
/// fileFingerprint member (the match level only carries "id", which nobody consumed).</summary>
public sealed record FlameFingerprintMatch(
    FlameModFile? File);

// ---------------------------------------------------------------------- serializer

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlameFingerprintsRequest))]
[JsonSerializable(typeof(FlameModsRequest))]
[JsonSerializable(typeof(FlameFilesRequest))]
[JsonSerializable(typeof(FlameStringEnvelope))]
[JsonSerializable(typeof(FlameFileList))]
[JsonSerializable(typeof(FlameFileEnvelope))]
[JsonSerializable(typeof(FlameProjectList))]
[JsonSerializable(typeof(FlameProjectEnvelope))]
[JsonSerializable(typeof(FlameCategoryList))]
[JsonSerializable(typeof(FlameFingerprintResponse))]
public sealed partial class FlameJsonContext : JsonSerializerContext;
