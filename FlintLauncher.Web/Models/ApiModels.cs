// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
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

/// <summary>One entry of GET /instances (the wire shape): every handle of the
/// addressing chain is exposed — uuid (the only persistent reference form), inode (0 on
/// platforms without stat inode support, e.g. Windows) and dirName (compat/display; the
/// "id" field keeps serving the same dirname so existing clients do not break the
/// chain).</summary>
public sealed record InstanceSummary(
    string Id,
    string Uuid,
    long Inode,
    string DirName,
    string Name,
    string IconKey,
    string Group,
    long LastLaunchTime,
    long TotalTimePlayed,
    bool Running,
    string InstanceType);

public sealed record InstanceDetail(
    string Id,
    string Uuid,
    long Inode,
    string DirName,
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

public sealed record ImportRequest(
    string Url,
    string? Name = null,
    Dictionary<string, bool>? OptionalMods = null);

public sealed record TaskAcceptedResponse(string TaskId);

public sealed record PatchInstanceRequest(
    string? Name,
    string? IconKey,
    string? Notes,
    string? Group);

// ------------------------------------------------------------------ settings

public sealed record SettingsPutRequest(
    [property: JsonPropertyName("overrides")]
    JsonElement Overrides);

// ------------------------------------------------------------------ launch / tasks

public sealed record LaunchRequest(
    string? Mode,
    string? OfflineName,
    string? AccountProfileId,
    LaunchJoinServer? Server = null,
    string? World = null);

/// <summary>POST /instances/{id}/launch optional direct join target ("server": {address,
/// port?}): bypasses the instance JoinServerOnLaunch setting (LaunchController.setTargetToJoin).
/// port omitted = Minecraft's address parsing default (25565, with SRV lookup).</summary>
public sealed record LaunchJoinServer(string Address, int? Port = null);

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
/// Installed, the export tasks set OutputPath).</summary>
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

// ------------------------------------------------------------------ java / modplatform

public sealed record JavaView(string Path, string Version, string Architecture);

public sealed record ModrinthSearchResultView(
    string Id,
    string Slug,
    string Title,
    string IconUrl,
    string Author,
    long Downloads);

// ------------------------------------------------------------------ modplatform

/// <summary>A CurseForge search hit mapped through FlameMod.LoadIndexedPack.</summary>
public sealed record FlameSearchResult(
    string Id,
    string Slug,
    string Name,
    string IconUrl,
    string? Author,
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

/// <summary>POST /instances/{id}/mods/update optional body (filter by installed file names).
/// ReleaseTypes are the channels allowed for this run ("Release"/"Beta"/"Alpha",
/// case-insensitive); when omitted the global ModUpdateReleaseTypes setting applies.</summary>
public sealed record ModsUpdateRequest(
    IReadOnlyList<string>? Mods,
    IReadOnlyList<string>? ReleaseTypes = null);

/// <summary>A provider category (Flame /categories or Modrinth /tag/category; Modrinth has
/// no numeric ids, so the id is the category name like the core LoadCategories mapping).</summary>
public sealed record ProviderCategoryView(string Id, string Name, string Icon);

/// <summary>The plain-text body of a project description / file changelog.</summary>
public sealed record ProviderBodyView(string Body);

// ------------------------------------------------------------------ instance content

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

/// <summary>POST /instances/{id}/worlds/{folderName}/copy body (the WorldPage "copy" dialog
/// name): the copy is placed inside the same saves folder and renamed on disk.</summary>
public sealed record WorldCopyRequest(string Name);

/// <summary>GET /instances/{id}/worlds/{folderName}: the WorldView fields plus the parsed
/// level.dat seed (WorldGenSettings.seed / RandomSeed, 0 when absent) and the icon.png path
/// (empty when the world has no icon).</summary>
public sealed record WorldDetailView(
    string Name,
    string FolderName,
    long LastPlayed,
    string GameType,
    string ContainerPath,
    long SizeBytes,
    long Seed,
    string IconPath);

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

// ------------------------------------------------------------------ components / export / settings

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
    string? Format,
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
public sealed record ManagedPackUpdateResponse(
    string CurrentVersion,
    string LatestVersion,
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

// ------------------------------------------------------------------ paste / about / meta cache

/// <summary>POST /api/v1/paste body: either the raw "content" or a "taskId" whose buffered
/// task log becomes the paste body (the GuiUtil "upload log" flow).</summary>
public sealed record PasteRequest(string? Content, string? TaskId);

/// <summary>POST /api/v1/paste response.</summary>
public sealed record PasteCreatedResponse(string Url);

/// <summary>GET /api/v1/about: the static launcher identity (BuildConfig port).</summary>
public sealed record AboutResponse(
    string Name,
    string Version,
    string Copyright,
    string MatrixUrl,
    string DiscordUrl,
    string SubredditUrl,
    string BugTrackerUrl);

/// <summary>One registered external profiler: its registry key, display name, whether the
/// configured path passes the factory's check, and the reason when it does not.</summary>
public sealed record ProfilerView(
    string Key,
    string Name,
    bool Available,
    string Error);

/// <summary>GET /api/v1/tools/profilers: the registered profilers and, for the one an instance
/// selects, the command the host would run. Nothing is started — the argv is the deliverable.</summary>
public sealed record ProfilerListView(
    ProfilerView[] Profilers,
    string Selected,
    ProfilerCommandView? Command);

/// <summary>The program and arguments a profiler would be started with for one pid.</summary>
public sealed record ProfilerCommandView(string Program, string[] Arguments);

// ----------------------------------------------------------------- ATLauncher

/// <summary>One packsnew.json index version (GET /modplatform/atlauncher/packs[/versions]).</summary>
public sealed record AtLauncherPackVersionView(string Version, string Minecraft);

/// <summary>One packsnew.json index pack, decoded through AtLauncherJson.LoadPackIndex
/// (safeName is the normalized pack *image* name, "&lt;safe&gt;.png").</summary>
public sealed record AtLauncherPackView(
    string Name,
    string SafeName,
    string Description,
    string Type,
    IReadOnlyList<AtLauncherPackVersionView> Versions);

/// <summary>POST /modplatform/atlauncher/install body: the pack to install (name+version),
/// the new instance's metadata and the pre-set dialog answers (optional-mod selection and
/// the loader "choose" mode's version). serverUrl overrides the ATL download-server static
/// for the duration of the call (tests / mirrors).</summary>
public sealed record AtLauncherInstallRequest(
    string Name,
    string Version,
    string? Group = null,
    string? IconKey = null,
    Dictionary<string, bool>? OptionalMods = null,
    string? ChooseLoaderVersion = null,
    string? ServerUrl = null);

// ----------------------------------------------------------------- FTB

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

/// <summary>The /modpack/{id} description decoded through FtbJson.LoadModpack
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

// ----------------------------------------------------------------- Technic

/// <summary>One search/trending hit mapped through TechnicPackIndex.LoadSearchResults (id is the
/// slug; displayName is only set by the single-pack mode, where the API distinguishes the
/// slug "name" from the "displayName"; iconUrl is null for the "null" logo placeholder).</summary>
public sealed record TechnicSearchResultView(
    string Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IconUrl);

/// <summary>GET /modplatform/technic/modpack/{slug}: the Platform metadata merged with the
/// Solder build listing. isSolder marks the Solder detection (the "solder" field presence);
/// url is the zip download URL for zip packs and the stripped Solder base for Solder packs;
/// versions/recommended/latest come from the Solder listing (zip packs only have the
/// platform's "version" field).</summary>
public sealed record TechnicPackView(
    string Slug,
    string Name,
    bool IsSolder,
    string Url,
    string MinecraftVersion,
    string WebsiteUrl,
    string Author,
    string Description,
    string CurrentVersion,
    IReadOnlyList<string> Versions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Recommended,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Latest);

/// <summary>POST /modplatform/technic/install body — two shapes: {url, minecraftVersion?}
/// (zip pack download) or {solder, slug, build?} (Solder instance; build omitted = the
/// listing's recommended/latest build). name/iconKey/group follow the other install
/// endpoints; serverUrl overrides the Technic API base static for the duration of the
/// call (tests / mirrors).</summary>
public sealed record TechnicInstallRequest(
    string? Url = null,
    string? Solder = null,
    string? Slug = null,
    string? Build = null,
    string? MinecraftVersion = null,
    string? Name = null,
    string? IconKey = null,
    string? Group = null,
    string? ServerUrl = null);

// ----------------------------------------------------------------- skins

/// <summary>GET /accounts/{profileId}/skin: the profile skin data the login flow stored on
/// the account (variant is the API's CLASSIC/SLIM spelling; empty when unknown).</summary>
public sealed record AccountSkinView(string Url, string Variant);

// ------------------------------------------------- skins (local skin library)

/// <summary>One entry of the local skin library (GET /skins). model is CLASSIC/SLIM;
/// capeId/url are the stored metadata (null when unknown); fileExists reports whether the
/// texture png is present in the skins dir.</summary>
public sealed record SkinView(string Name, string Model, string? CapeId, string? Url, bool FileExists);

/// <summary>POST /skins/{name}/apply body: which account wears the stored skin. model
/// overrides the stored variant ("classic"/"slim"); omitted = the stored one.</summary>
public sealed record SkinApplyRequest(string ProfileId, string? Model = null);

/// <summary>POST /skins response.</summary>
public sealed record SkinStoredResponse(string Name);
