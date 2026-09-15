// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web;

// NOTE: JsonElement/JsonNode(-derived) values are natively supported by System.Text.Json
// (no reflection), so they are deliberately NOT registered here — registering them trips
// SYSLIB1030/1031.

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
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
[JsonSerializable(typeof(NewsView))]
[JsonSerializable(typeof(ModrinthSearchResult))]
[JsonSerializable(typeof(FlameSearchResult))]
[JsonSerializable(typeof(ProviderVersionView))]
[JsonSerializable(typeof(InstalledModView))]
[JsonSerializable(typeof(ModInstallSelection))]
[JsonSerializable(typeof(List<ModInstallSelection>))]
[JsonSerializable(typeof(ModsUpdateRequest))]
[JsonSerializable(typeof(ModUpdateEntryView))]
[JsonSerializable(typeof(TaskResultView))]
// Wave A (UI-API mapping): instance content surface
[JsonSerializable(typeof(InstanceGroupView))]
[JsonSerializable(typeof(CopyInstanceRequest))]
[JsonSerializable(typeof(WorldView))]
[JsonSerializable(typeof(WorldRenameRequest))]
[JsonSerializable(typeof(ServerEntryView))]
[JsonSerializable(typeof(ServerAddRequest))]
[JsonSerializable(typeof(ServerPingView))]
[JsonSerializable(typeof(ResourceView))]
[JsonSerializable(typeof(ScreenshotView))]
[JsonSerializable(typeof(ScreenshotUploadRequest))]
[JsonSerializable(typeof(GameOptionView))]
[JsonSerializable(typeof(GameOptionsPutRequest))]
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
[JsonSerializable(typeof(NewsView[]))]
[JsonSerializable(typeof(ModrinthSearchResult[]))]
[JsonSerializable(typeof(JavaView[]))]
[JsonSerializable(typeof(FlameSearchResult[]))]
[JsonSerializable(typeof(ProviderVersionView[]))]
[JsonSerializable(typeof(InstalledModView[]))]
internal sealed partial class FlintJsonContext : JsonSerializerContext;
