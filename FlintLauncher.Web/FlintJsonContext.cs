// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.State;
using FlintLauncher.Core.Store;
using FlintLauncher.Web.Endpoints;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web;

// NOTE: JsonElement/JsonNode(-derived) values are natively supported by System.Text.Json
// (no reflection), so they are deliberately NOT registered here — registering them trips
// SYSLIB1030/1031.

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(AbsolutePathJsonConverter)])]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ApiErrorEnvelope))]
[JsonSerializable(typeof(InstanceSummary))]
[JsonSerializable(typeof(InstanceDetail))]
[JsonSerializable(typeof(VanillaCreateRequest))]
[JsonSerializable(typeof(InstanceCreatedResponse))]
[JsonSerializable(typeof(ImportRequest))]
[JsonSerializable(typeof(TaskAcceptedResponse))]
[JsonSerializable(typeof(PatchInstanceRequest))]
[JsonSerializable(typeof(SettingsPutRequest))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(TaskProgressDto))]
[JsonSerializable(typeof(TaskView))]
[JsonSerializable(typeof(LogLineDto))]
[JsonSerializable(typeof(TaskLogResponse))]
[JsonSerializable(typeof(AccountView))]
[JsonSerializable(typeof(OfflineAccountRequest))]
[JsonSerializable(typeof(DeviceCodeStartResponse))]
[JsonSerializable(typeof(DeviceCodePollResponse))]
[JsonSerializable(typeof(JavaView))]
[JsonSerializable(typeof(ModrinthSearchResultView))]
[JsonSerializable(typeof(FlameSearchResult))]
[JsonSerializable(typeof(ProviderVersionView))]
[JsonSerializable(typeof(InstalledModView))]
[JsonSerializable(typeof(ModInstallSelection))]
[JsonSerializable(typeof(List<ModInstallSelection>))]
[JsonSerializable(typeof(SettingsGroupsResponse))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(ModsUpdateRequest))]
[JsonSerializable(typeof(ModUpdateEntryView))]
[JsonSerializable(typeof(TaskResultView))]
[JsonSerializable(typeof(ProviderCategoryView))]
[JsonSerializable(typeof(ProviderBodyView))]
// ATLauncher: packsnew.json surface + install request
[JsonSerializable(typeof(AtLauncherPackView))]
[JsonSerializable(typeof(AtLauncherPackVersionView))]
[JsonSerializable(typeof(AtLauncherInstallRequest))]
// FTB: modpacks.ch surface + install request
[JsonSerializable(typeof(FtbPackIndexResponse))]
[JsonSerializable(typeof(FtbPackIndexEntry))]
[JsonSerializable(typeof(FtbPackView))]
[JsonSerializable(typeof(FtbPackVersionView))]
[JsonSerializable(typeof(FtbPackArtView))]
[JsonSerializable(typeof(FtbPackAuthorView))]
[JsonSerializable(typeof(FtbInstallRequest))]
// Technic: platform search/trending/metadata surface + install request
[JsonSerializable(typeof(TechnicSearchResultView))]
[JsonSerializable(typeof(TechnicPackView))]
[JsonSerializable(typeof(TechnicInstallRequest))]
// skins: the account skin surface
[JsonSerializable(typeof(AccountSkinView))]
// skins: the local skin library surface
[JsonSerializable(typeof(SkinView))]
[JsonSerializable(typeof(SkinApplyRequest))]
[JsonSerializable(typeof(SkinStoredResponse))]
[JsonSerializable(typeof(SkinView[]))]
// instance content surface
[JsonSerializable(typeof(InstanceGroupView))]
[JsonSerializable(typeof(CopyInstanceRequest))]
[JsonSerializable(typeof(WorldView))]
[JsonSerializable(typeof(WorldRenameRequest))]
[JsonSerializable(typeof(WorldCopyRequest))]
[JsonSerializable(typeof(WorldDetailView))]
[JsonSerializable(typeof(ServerEntryView))]
[JsonSerializable(typeof(ServerAddRequest))]
[JsonSerializable(typeof(ServerPingView))]
[JsonSerializable(typeof(ResourceView))]
[JsonSerializable(typeof(ScreenshotView))]
[JsonSerializable(typeof(ScreenshotUploadRequest))]
[JsonSerializable(typeof(GameOptionView))]
[JsonSerializable(typeof(GameOptionsPutRequest))]
// components / export / logs / managed pack / icons / settings
[JsonSerializable(typeof(ComponentProblemView))]
[JsonSerializable(typeof(ComponentView))]
[JsonSerializable(typeof(ComponentPatchRequest))]
[JsonSerializable(typeof(ComponentInstallRequest))]
[JsonSerializable(typeof(ComponentMoveRequest))]
[JsonSerializable(typeof(ExportRequest))]
[JsonSerializable(typeof(LogFileView))]
[JsonSerializable(typeof(ManagedPackView))]
[JsonSerializable(typeof(ManagedPackUpdateResponse))]
[JsonSerializable(typeof(IconView))]
[JsonSerializable(typeof(IconInstalledResponse))]
[JsonSerializable(typeof(InstanceIconPutRequest))]
[JsonSerializable(typeof(GlobalSettingsPutRequest))]
// launch join targets, paste, about
[JsonSerializable(typeof(LaunchJoinServer))]
[JsonSerializable(typeof(PasteRequest))]
[JsonSerializable(typeof(PasteCreatedResponse))]
[JsonSerializable(typeof(AboutResponse))]
// collection roots (standalone arrays returned from list endpoints are NOT auto-generated)
[JsonSerializable(typeof(InstanceSummary[]))]
[JsonSerializable(typeof(InstanceGroupView[]))]
[JsonSerializable(typeof(WorldView[]))]
[JsonSerializable(typeof(ServerEntryView[]))]
[JsonSerializable(typeof(ResourceView[]))]
[JsonSerializable(typeof(ScreenshotView[]))]
[JsonSerializable(typeof(GameOptionView[]))]
[JsonSerializable(typeof(TaskView[]))]
[JsonSerializable(typeof(AccountView[]))]
[JsonSerializable(typeof(ModrinthSearchResultView[]))]
[JsonSerializable(typeof(JavaView[]))]
[JsonSerializable(typeof(FlameSearchResult[]))]
[JsonSerializable(typeof(ProviderVersionView[]))]
[JsonSerializable(typeof(InstalledModView[]))]
[JsonSerializable(typeof(ProviderCategoryView[]))]
[JsonSerializable(typeof(ComponentView[]))]
[JsonSerializable(typeof(LogFileView[]))]
[JsonSerializable(typeof(IconView[]))]
// collection roots for the ATLauncher list endpoints (standalone arrays are NOT auto-generated)
[JsonSerializable(typeof(AtLauncherPackView[]))]
[JsonSerializable(typeof(AtLauncherPackVersionView[]))]
// collection roots for the FTB list endpoints (standalone arrays are NOT auto-generated)
[JsonSerializable(typeof(FtbPackVersionView[]))]
[JsonSerializable(typeof(FtbPackArtView[]))]
[JsonSerializable(typeof(FtbPackAuthorView[]))]
// collection roots for the Technic list endpoints (standalone arrays are NOT auto-generated)
[JsonSerializable(typeof(TechnicSearchResultView[]))]
// migration layer (Prism→Flint import): request body + plan items
[JsonSerializable(typeof(PrismImportRequest))]
[JsonSerializable(typeof(PrismDeclineRequest))]
[JsonSerializable(typeof(PrismSourceDetector.PrismSourceCandidate[]))]
[JsonSerializable(typeof(PrismImportPlanResponse))]
[JsonSerializable(typeof(PrismImportDryRunResponse))]
[JsonSerializable(typeof(PrismImportPlanItem))]
// migration decision records — TypedJsonFile root
[JsonSerializable(typeof(MigrationDecisionRecord))]
// store completeness: the derived {complete, missing[], bytes} report
[JsonSerializable(typeof(StoreStatusReport))]
// launcher-state.json — TypedJsonFile roots
[JsonSerializable(typeof(LauncherState))]
[JsonSerializable(typeof(PlaytimeState))]
[JsonSerializable(typeof(JavaDetectState))]
[JsonSerializable(typeof(JavaDetectEntry))]
[JsonSerializable(typeof(UiState))]
// secrets.json — TypedJsonFile root
[JsonSerializable(typeof(SecretsFile))]
// flint.json — TypedJsonFile root: the record domain full set.
// A missing root here is a RUNTIME 500, not a compile error (AOT red line)
[JsonSerializable(typeof(FlintConfigFile))]
[JsonSerializable(typeof(LauncherConfig))]
[JsonSerializable(typeof(ConsoleConfig))]
[JsonSerializable(typeof(GameTimeConfig))]
[JsonSerializable(typeof(WindowConfig))]
[JsonSerializable(typeof(MemoryConfig))]
[JsonSerializable(typeof(PerformanceConfig))]
[JsonSerializable(typeof(NativeLibConfig))]
[JsonSerializable(typeof(CommandsConfig))]
[JsonSerializable(typeof(MinecraftMiscConfig))]
[JsonSerializable(typeof(ModDownloadConfig))]
[JsonSerializable(typeof(JavaConfig))]
[JsonSerializable(typeof(DirsConfig))]
[JsonSerializable(typeof(LanguageConfig))]
[JsonSerializable(typeof(MirrorConfig))]
[JsonSerializable(typeof(ProxyConfig))]
[JsonSerializable(typeof(ApiConfig))]
[JsonSerializable(typeof(ToolsConfig))]
// instance.json — TypedJsonFile root: the record domain full set. A missing root
// here is a RUNTIME 500, not a compile error (AOT red line)
[JsonSerializable(typeof(InstanceConfigFile))]
[JsonSerializable(typeof(InstanceIdentityConfig))]
[JsonSerializable(typeof(InstanceLaunchConfig))]
[JsonSerializable(typeof(InstanceJavaConfig))]
[JsonSerializable(typeof(InstanceMemoryConfig))]
[JsonSerializable(typeof(InstanceWindowConfig))]
[JsonSerializable(typeof(InstanceCommandsConfig))]
[JsonSerializable(typeof(InstancePerformanceConfig))]
[JsonSerializable(typeof(InstanceNativeLibConfig))]
[JsonSerializable(typeof(InstanceManagedConfig))]
[JsonSerializable(typeof(InstanceExportConfig))]
[JsonSerializable(typeof(InstanceMiscConfig))]
[JsonSerializable(typeof(InstanceConsoleConfig))]
[JsonSerializable(typeof(InstanceGameTimeConfig))]
// instance-state.json — TypedJsonFile root (the per-instance runtime time trio)
[JsonSerializable(typeof(InstanceStateFile))]
internal sealed partial class FlintJsonContext : JsonSerializerContext;
