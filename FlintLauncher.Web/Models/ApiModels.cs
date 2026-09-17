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

/// <summary>One applied/found mod update, exposed through TaskView.Result (mods-update).</summary>
public sealed record ModUpdateEntryView(string File, string From, string To, string Provider);

public sealed record ModInstallEntryView(
    string File,
    string Provider,
    string ProjectId,
    string Version,
    bool IsDependency,
    string? RequiredBy);

/// <summary>Optional terminal payload of a task (mods-update sets Updates, mods-install sets
/// Installed, the Wave B export tasks set OutputPath).</summary>
public sealed record TaskResultView(
    IReadOnlyList<ModUpdateEntryView>? Updates,
    IReadOnlyList<ModInstallEntryView>? Installed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? OutputPath = null);

public sealed record TaskView(
    string Id,
    string Kind,
    string State,
    string Status,
    TaskProgressDto Progress,
    string? FailReason,
    string? InstanceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TaskResultView? Result = null);

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

// ------------------------------------------------------------------ modplatform (P6-fix-b)

/// <summary>A CurseForge search hit mapped through FlameMod.LoadIndexedPack.</summary>
public sealed record FlameSearchResult(
    string Id,
    string Slug,
    string Name,
    string IconUrl,
    string Author,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? Downloads);

/// <summary>An indexed project version (Modrinth /project/{id}/version or Flame /mods/{id}/files).</summary>
public sealed record ProviderVersionView(
    string Id,
    string VersionNumber,
    string Date,
    string Filename,
    string DownloadUrl,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<string> McVersions);

/// <summary>A mod installed in an instance's mods folder.</summary>
public sealed record InstalledModView(
    string FileName,
    string? Name,
    string? Version,
    string? Provider,
    bool Enabled);

/// <summary>POST /instances/{id}/mods/install body entry.</summary>
public sealed record ModInstallSelection(string Provider, string ProjectId, string? VersionId);

/// <summary>POST /instances/{id}/mods/update optional body (filter by installed file names).</summary>
public sealed record ModsUpdateRequest(IReadOnlyList<string>? Mods);

/// <summary>A provider category (Flame /categories or Modrinth /tag/category; Modrinth has
/// no numeric ids, so the id is the category name like the core LoadCategories mapping).</summary>
public sealed record ProviderCategoryView(string Id, string Name, string Icon);

/// <summary>The plain-text body of a project description / file changelog.</summary>
public sealed record ProviderBodyView(string Body);

// ------------------------------------------------------------------ UI-API Wave A

/// <summary>One named group of the instance list (GET /instances/groups).</summary>
public sealed record InstanceGroupView(string Name, IReadOnlyList<string> InstanceIds);

/// <summary>POST /instances/{id}/copy body: the display name plus the CopyInstanceDialog
/// checkbox set (null = keep the InstanceCopyPrefs default).</summary>
public sealed record CopyInstanceRequest(
    string Name,
    string? Group = null,
    string? IconKey = null,
    bool? CopySaves = null,
    bool? KeepPlaytime = null,
    bool? CopyGameOptions = null,
    bool? CopyResourcePacks = null,
    bool? CopyShaderPacks = null,
    bool? CopyServers = null,
    bool? CopyMods = null,
    bool? CopyScreenshots = null,
    bool? UseSymLinks = null,
    bool? LinkRecursively = null,
    bool? UseHardLinks = null,
    bool? DontLinkSaves = null,
    bool? UseClone = null);

/// <summary>One world of an instance's saves folder (GET /instances/{id}/worlds).</summary>
public sealed record WorldView(
    string Name,
    string FolderName,
    long LastPlayed,
    string GameType,
    string ContainerPath,
    long SizeBytes);

/// <summary>POST /instances/{id}/worlds/{folderName}/rename body.</summary>
public sealed record WorldRenameRequest(string NewName);

/// <summary>One entry of the instance's servers.dat (GET /instances/{id}/servers).</summary>
public sealed record ServerEntryView(
    int Index,
    string Name,
    string Address,
    string AcceptsTextures,
    bool HasIcon);

/// <summary>POST /instances/{id}/servers body.</summary>
public sealed record ServerAddRequest(string Name, string Address);

/// <summary>POST /instances/{id}/servers/{index}/ping response: the SRV/A resolution outcome
/// plus the fields the status handshake extracts from the server's JSON.</summary>
public sealed record ServerPingView(
    string Host,
    int Port,
    string ResolvedIp,
    int ResolvedPort,
    int OnlinePlayers,
    int MaxPlayers,
    string? Version,
    string? Motd,
    long LatencyMs);

/// <summary>One resource of an instance resource folder (GET /instances/{id}/resources/{type}).</summary>
public sealed record ResourceView(
    string FileName,
    string Name,
    bool Enabled,
    string Type,
    long SizeBytes,
    long ModifiedAt);

/// <summary>One screenshot file of an instance's screenshots folder.</summary>
public sealed record ScreenshotView(string Name, long SizeBytes, long ModifiedAt);

/// <summary>POST /instances/{id}/screenshots/upload body (file names of the listing).</summary>
public sealed record ScreenshotUploadRequest(IReadOnlyList<string> Names);

/// <summary>One options.txt entry (GET /instances/{id}/gameoptions).</summary>
public sealed record GameOptionView(string Key, string Value);

/// <summary>PUT /instances/{id}/gameoptions body: key/values to set (add-or-replace).</summary>
public sealed record GameOptionsPutRequest(IReadOnlyList<GameOptionView> Options);

// ------------------------------------------------------------------ UI-API Wave B

/// <summary>One problem of a component (GET /instances/{id}/components, the VersionPage
/// problem column).</summary>
public sealed record ComponentProblemView(string Severity, string Description);

/// <summary>One row of an instance's component stack (the VersionPage list).</summary>
public sealed record ComponentView(
    string Uid,
    string? Version,
    string? Name,
    bool Important,
    bool Customized,
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ComponentProblemView[]? Problems);

/// <summary>PATCH /instances/{id}/components/{uid} body: change the pinned version and/or
/// the important flag.</summary>
public sealed record ComponentPatchRequest(string? Version, bool? Important);

/// <summary>POST /instances/{id}/components body: install a loader component
/// (SetComponentVersion with important=true unless the body says otherwise).</summary>
public sealed record ComponentInstallRequest(string Uid, string? Version, bool? Important);

/// <summary>POST /instances/{id}/components/{uid}/move body: -1 moves up, 1 moves down.</summary>
public sealed record ComponentMoveRequest(int Delta);

/// <summary>POST /instances/{id}/export body (ExportInstanceDialog/ExportPackDialog/
/// ExportToModListDialog inputs).</summary>
public sealed record ExportRequest(
    string Format,
    string? Name = null,
    string? VersionId = null,
    string? Summary = null,
    string? Author = null,
    string? Format2 = null);

/// <summary>One log/crash-report file of an instance (GET /instances/{id}/logs).</summary>
public sealed record LogFileView(string Name, string Dir, long SizeBytes, long ModifiedAt);

/// <summary>GET /instances/{id}/managedpack response (ManagedPackPage fields).</summary>
public sealed record ManagedPackView(
    bool Managed,
    string Type,
    string Id,
    string Name,
    string VersionId,
    string VersionName);

/// <summary>POST /instances/{id}/managedpack/update-check response.</summary>
public sealed record ManagedPackUpdateResponse(string CurrentVersion, string LatestVersion,
    bool UpdateAvailable);

/// <summary>One icon of the global icon library (GET /api/v1/icons).</summary>
public sealed record IconView(string Key, string Name);

/// <summary>POST /api/v1/icons response.</summary>
public sealed record IconInstalledResponse(string Key);

/// <summary>PUT /instances/{id}/icon JSON body (multipart upload is the other variant).</summary>
public sealed record InstanceIconPutRequest(string IconKey);

/// <summary>PUT /api/v1/settings body: the group plus the typed key/value map to write.</summary>
public sealed record GlobalSettingsPutRequest(string Group, JsonElement Values);

public sealed record SettingsGroupsResponse(IReadOnlyDictionary<string, object> Groups);

// ----------------------------------------------------------------- ATLauncher (fix-2)

/// <summary>One packsnew.json index version (GET /modplatform/atlauncher/packs[/versions]).</summary>
public sealed record AtLauncherPackVersionView(string Version, string Minecraft);

/// <summary>One packsnew.json index pack, mapped through AtLauncherPackIndex.LoadIndexedPack
/// (safeName is the normalized pack *image* name, "&lt;safe&gt;.png").</summary>
public sealed record AtLauncherPackView(string Name, string SafeName, string Description, string Type,
    IReadOnlyList<AtLauncherPackVersionView> Versions);

/// <summary>POST /modplatform/atlauncher/install body: the pack to install (name+version),
/// the new instance's metadata and the pre-set dialog answers (optional-mod selection and
/// the loader "choose" mode's version). serverUrl overrides the ATL download-server static
/// for the duration of the call (tests / mirrors).</summary>
public sealed record AtLauncherInstallRequest(string Name, string Version, string? Group = null,
    string? IconKey = null, Dictionary<string, bool>? OptionalMods = null,
    string? ChooseLoaderVersion = null, string? ServerUrl = null);

// ----------------------------------------------------------------- FTB (fix-2b)

/// <summary>One /modpack/all index entry — the index payload only carries pack ids.</summary>
public sealed record FtbPackIndexEntry(int Id);

/// <summary>GET /modplatform/ftb/packs response: the id list of the /modpack/all index.</summary>
public sealed record FtbPackIndexResponse(IReadOnlyList<FtbPackIndexEntry> Packs);

/// <summary>One pack version (GET /modplatform/ftb/packs/{id}[/versions]).</summary>
public sealed record FtbPackVersionView(int Id, string Name, string Type, long Updated);

/// <summary>One pack art entry (the square/square-2x logo entries of the description).</summary>
public sealed record FtbPackArtView(string Url, string Type, int Width, int Height);

/// <summary>One pack author (GET /modplatform/ftb/packs/{id}).</summary>
public sealed record FtbPackAuthorView(string Name, string Type, string Website);

/// <summary>The /modpack/{id} description mapped through FtbPackManifest.LoadModpack
/// (safeName is the normalized pack *image* name, "&lt;safe&gt;.png"; tags are flattened to
/// their names).</summary>
public sealed record FtbPackView(
    int Id,
    string Name,
    string Synopsis,
    string Description,
    string Type,
    bool Featured,
    int Installs,
    int Plays,
    long Updated,
    string SafeName,
    IReadOnlyList<FtbPackArtView> Art,
    IReadOnlyList<FtbPackAuthorView> Authors,
    IReadOnlyList<FtbPackVersionView> Versions,
    IReadOnlyList<string> Tags);

/// <summary>POST /modplatform/ftb/install body: the pack/version to install plus the new
/// instance's metadata. versionId omitted = the pack's latest (first-listed) version;
/// serverUrl overrides the FTB API base static for the duration of the call (tests /
/// mirrors).</summary>
public sealed record FtbInstallRequest(
    int PackId,
    int? VersionId = null,
    string? Name = null,
    string? IconKey = null,
    string? Group = null,
    string? ServerUrl = null);
