// SPDX-License-Identifier: GPL-3.0-only
// API DTOs. Explicit records (no dictionaries of object) so the whole surface stays
// source-generation/AOT clean; every type here is registered in FlintJsonContext.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlintLauncher.Web.Models;

// ------------------------------------------------------------------ meta

public sealed record HealthResponse(string Status);

public sealed record InfoResponse(
    string Name,
    string Version,
    string UserAgent,
    string MetaUrl,
    bool JavaDownloaderEnabled);

public sealed record ErrorResponse(string Code, string Message, string? Detail = null);

/// <summary>Uniform error envelope: {"error":{"code":"...","message":"..."}}.</summary>
public sealed record ApiErrorEnvelope(ErrorResponse Error);

// ------------------------------------------------------------------ instances

public sealed record InstanceSummary(
    string Id,
    string Name,
    string IconKey,
    string Group,
    long LastLaunchTime,
    long TotalTimePlayed,
    bool Running,
    string InstanceType);

public sealed record InstanceDetail(
    string Id,
    string Name,
    string IconKey,
    string Group,
    long LastLaunchTime,
    long TotalTimePlayed,
    bool Running,
    string InstanceType,
    string Notes,
    string GameRoot);

public sealed record VanillaCreateRequest(
    string Name,
    string McVersion,
    string? IconKey,
    string? Group);

public sealed record InstanceCreatedResponse(string Id, string Name);

public sealed record ImportRequest(string Url, string? Name = null);

public sealed record TaskAcceptedResponse(string TaskId);

public sealed record PatchInstanceRequest(
    string? Name,
    string? IconKey,
    string? Notes,
    string? Group);

// ------------------------------------------------------------------ settings

public sealed record SettingsPutRequest(
    [property: JsonPropertyName("overrides")] JsonElement Overrides);

// ------------------------------------------------------------------ launch / tasks

public sealed record LaunchRequest(string Mode, string? OfflineName, string? AccountProfileId);

public sealed record TaskProgressDto(long Current, long Total);

public sealed record TaskView(
    string Id,
    string Kind,
    string State,
    string Status,
    TaskProgressDto Progress,
    string? FailReason,
    string? InstanceId);

public sealed record LogLineDto(int Index, string Level, string Line);

public sealed record TaskLogResponse(IReadOnlyList<LogLineDto> Lines);

// ------------------------------------------------------------------ accounts

public sealed record AccountView(string ProfileId, string Name, string Type, bool IsDefault);

public sealed record OfflineAccountRequest(string Name);

public sealed record DeviceCodeStartResponse(
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    string DeviceId);

public sealed record DeviceCodePollResponse(string State, AccountView? Account);

// ------------------------------------------------------------------ java / news / modplatform

public sealed record JavaView(string Path, string Version, string Architecture);

public sealed record NewsView(string Title, string Content, string Link);

public sealed record ModrinthSearchResult(
    string Id,
    string Slug,
    string Title,
    string IconUrl,
    string Author,
    long Downloads);
