// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Instance endpoints: list/get/create-vanilla/import/delete/patch + instance settings.

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Instances.Creation;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;
using TruePath;

namespace FlintLauncher.Web.Endpoints;

public static class InstanceEndpoints
{
    public static void MapInstances(this RouteGroupBuilder api)
    {
        api.WithTags("Instances");
        api.MapGet("/instances", async (InstanceList instances, InstanceMutationLock instanceLock,
            string search = "", string group = "") =>
        {
            await instanceLock.WaitAsync();
            try
            {
                // MainWindow's instance-view filter: name contains + group equals (both
                // case-insensitive; the group match is exact except for casing)
                IEnumerable<InstanceSummary> summaries = instances.Instances
                    .Select(inst => Summary(inst, instances));
                if (!string.IsNullOrEmpty(search))
                {
                    summaries = summaries.Where(summary =>
                        summary.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(group))
                {
                    summaries = summaries.Where(summary =>
                        string.Equals(summary.Group, group, StringComparison.OrdinalIgnoreCase));
                }

                return Results.Ok(summaries.ToArray());
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<InstanceSummary[]>();

        // the group index of the instance list (MainWindow's group view)
        api.MapGet("/instances/groups", async (InstanceList instances,
            InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                var byGroup = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var inst in instances.Instances)
                {
                    var group = instances.GetInstanceGroup(inst.Id());
                    if (group.Length == 0)
                    {
                        continue; // ungrouped instances are not a group
                    }

                    if (!byGroup.TryGetValue(group, out var uuids))
                    {
                        uuids = new List<string>();
                        byGroup[group] = uuids;
                    }

                    uuids.Add(inst.Uuid);
                }

                return Results.Ok(byGroup.Select(pair =>
                    new InstanceGroupView(pair.Key, pair.Value.OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray())).ToArray());
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<InstanceGroupView[]>();

        // {ref} resolves through the addressing chain (inode → uuid → dirname); direct
        // GetInstanceById is banned at the API boundary
        api.MapGet("/instances/{ref}", async (string @ref,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                var inst = instances.ResolveInstance(@ref);
                return inst is null
                    ? ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'")
                    : Results.Ok(Detail(inst, instances));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<InstanceDetail>();

        api.MapPost("/instances/vanilla", async (VanillaCreateRequest request,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.McVersion))
            {
                return ApiJson.Error("invalid_body", "'name' and 'mcVersion' are required", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                // staged-commit path: VanillaCreationTask runs inside an InstanceList staging
                // folder; InstanceStaging commits the folder into instances/ on success. The
                // flow is quick and offline-safe (only the component list is written), so the
                // endpoint runs it synchronously and returns the committed instance id.
                var task = new VanillaCreationTask(request.McVersion)
                {
                    TargetDir = default,
                    Group = request.Group ?? "",
                };
                task.SetName(request.Name);
                task.Icon = request.IconKey ?? "default";

                var wrapped = instances.WrapInstanceTask(task);
                // the commit derives the folder from the display name: compute the expected
                // id BEFORE running the task (after the commit the directory already exists
                // and DirNameFromString would return "<name>(1)")
                AbsolutePath inDir = new AbsolutePath(instances.PrimaryDir());
                var expectedId = FileNaming.DirNameFromString(request.Name, [inDir]);
                await wrapped.StartAsync();
                if (!wrapped.WasSuccessful)
                {
                    return ApiJson.Error("creation_failed", wrapped.FailReason, 500);
                }

                instances.LoadList();
                var inst = instances.ResolveInstance(expectedId);
                return inst is null
                    ? ApiJson.Error("creation_failed", "instance was committed but not discovered", 500)
                    : Results.Created($"/api/v1/instances/{inst.Uuid}",
                        new InstanceCreatedResponse(inst.Id(), inst.Name()));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<InstanceCreatedResponse>(201);

        api.MapPost("/instances/import", async (ImportRequest request, InstanceList instances,
            InstanceMutationLock instanceLock, ModrinthClient modrinth, HttpClient http, FlameClient flame, HttpMetaCache metaCache, FlintTaskRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url) ||
                !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "file"))
            {
                return ApiJson.Error("invalid_url", "'url' must be an absolute http(s)/file URL", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                var importTask = new InstanceImportTask(uri, trustedSource: true,
                    modrinth: modrinth, http: http, flame: flame)
                {
                    MetaCache = metaCache,
                    // the OptionalModDialog answers for mrpack/CurseForge packs:
                    // keys are the manifest file paths, unselected files install disabled
                    OptionalModsSelection = request.OptionalMods ?? new Dictionary<string, bool>(),
                };
                // optional display name: without one the committed folder cannot be derived
                // (the C++ always has the pack name from the import dialog)
                if (!string.IsNullOrWhiteSpace(request.Name))
                {
                    importTask.SetName(request.Name);
                }

                var wrapped = instances.WrapInstanceTask(importTask);
                // NOTE: the hook runs via Task.Run — the fire-and-forget StartAsync below can
                // complete synchronously on this thread (small/file packs), and an inline
                // InstanceLock.Wait() here would deadlock against this very handler
                var entry = registry.Register("import", wrapped, onFinished: () => _ = Task.Run(() =>
                {
                    instanceLock.Wait();
                    try
                    {
                        instances.LoadList();
                    }
                    finally
                    {
                        instanceLock.Release();
                    }
                }));
                _ = wrapped.StartAsync(); // 202: downloads/modpack processing run in background
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<TaskAcceptedResponse>(202);

        // CopyInstanceDialog: a selective copy runs as a staged instance task (the copy is
        // committed into instances/ on success) and is tracked like import
        api.MapPost("/instances/{ref}/copy", async (string @ref, CopyInstanceRequest request,
            InstanceList instances, InstanceMutationLock instanceLock,
            FlintTaskRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiJson.Error("invalid_body", "'name' is required", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                var original = instances.ResolveInstance(@ref);
                if (original is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'");
                }

                // the dialog checkboxes; unspecified flags keep the InstanceCopyPrefs defaults
                var prefs = new InstanceCopyPrefs
                {
                    CopySaves = request.CopySaves ?? true,
                    KeepPlaytime = request.KeepPlaytime ?? true,
                    CopyGameOptions = request.CopyGameOptions ?? true,
                    CopyResourcePacks = request.CopyResourcePacks ?? true,
                    CopyShaderPacks = request.CopyShaderPacks ?? true,
                    CopyServers = request.CopyServers ?? true,
                    CopyMods = request.CopyMods ?? true,
                    CopyScreenshots = request.CopyScreenshots ?? true,
                    UseSymLinks = request.UseSymLinks ?? false,
                    LinkRecursively = request.LinkRecursively ?? false,
                    UseHardLinks = request.UseHardLinks ?? false,
                    DontLinkSaves = request.DontLinkSaves ?? false,
                    UseClone = request.UseClone ?? false,
                };

                var task = new InstanceCopyTask(original, prefs)
                {
                    TargetDir = default,
                    Group = request.Group ?? "",
                };
                task.SetName(request.Name);
                // the dialog copies the original's icon when none was picked (and the copy
                // carries the original's iconKey inside instance.cfg either way)
                task.Icon = request.IconKey ?? original.IconKey();

                var wrapped = instances.WrapInstanceTask(task);
                var entry = registry.Register("copy", wrapped, instanceId: @ref,
                    onFinished: () => _ = Task.Run(() =>
                    {
                        instanceLock.Wait();
                        try
                        {
                            instances.LoadList();
                        }
                        finally
                        {
                            instanceLock.Release();
                        }
                    }));
                _ = wrapped.StartAsync(); // 202: the folder copy + commit run in the background
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<TaskAcceptedResponse>(202);

        api.MapDelete("/instances/{ref}", async (string @ref,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                var inst = instances.ResolveInstance(@ref);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'");
                }

                instances.DeleteInstance(inst.Id());
                return Results.NoContent();
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);

        // a rename is a pure METADATA write — the directory name stays (it is the
        // display form, not the identity); references hold through the chain (uuid),
        // so there is no directory move, no LoadList reload and no group re-attachment
        // (the old syncInstanceDirName flow is gone).
        api.MapPatch("/instances/{ref}", async (string @ref, PatchInstanceRequest request,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                var inst = instances.ResolveInstance(@ref);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'");
                }

                if (request.Name is { } name && name.Length != 0 && name != inst.Name())
                {
                    inst.SetName(name);
                }

                if (request.IconKey is { } icon)
                {
                    inst.SetIconKey(icon);
                }

                if (request.Notes is { } notes)
                {
                    inst.SetNotes(notes);
                }

                if (request.Group is { } group)
                {
                    instances.SetInstanceGroup(inst.Id(), group);
                }

                instances.SaveNow();
                return Results.NoContent();
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);

        // ------------------------------------------------------------ instance settings
        api.MapGet("/instances/{ref}/settings", async (string @ref,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                var inst = instances.ResolveInstance(@ref);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'");
                }

                var settings = inst.Settings;
                var node = new JsonObject();
                if (settings is InstanceJsonSettingsObject typed)
                {
                    // the instance's own layer: the derived gate states, every
                    // instance-layer member that is set (with its EFFECTIVE value — the
                    // fold over flint.json) and the runtime time trio (instance-state.json).
                    // Keys the instance layer never set are absent — they inherit.
                    foreach (var entry in InstanceConfigKeys.All
                                 .OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        var derivedGate = entry.Key.StartsWith("Override", StringComparison.Ordinal);
                        // NullInstance only registers the BaseInstance key set — type-specific
                        // keys that never registered are absent, like un-set ones
                        if ((derivedGate || entry.IsSet(typed.ConfigStore.Current)) &&
                            settings.GetSetting(entry.Key) is { } setting)
                        {
                            node[entry.Key] = ToNode(TypedByDefaultKind(setting));
                        }
                    }

                    node["lastLaunchTime"] = typed.StateStore.Current.LastLaunchTime;
                    node["lastTimePlayed"] = typed.StateStore.Current.LastTimePlayed;
                    node["totalTimePlayed"] = typed.StateStore.Current.TotalTimePlayed;
                }

                var dict = new Dictionary<string, JsonElement>();
                foreach (var pair in node)
                {
                    dict[pair.Key] = pair.Value is null
                        ? JsonDocument.Parse("null").RootElement.Clone()
                        : JsonDocument.Parse(pair.Value.ToJsonString()).RootElement.Clone();
                }

                // flat wire shape (the keys at the top level, exactly what ApiJson.Node used
                // to write) — a wrapper record would nest it behind a "settings" property
                return Results.Ok(dict);
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<Dictionary<string, JsonElement>>();

        api.MapPut("/instances/{ref}/settings", async (string @ref, SettingsPutRequest request,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            if (request.Overrides.ValueKind != JsonValueKind.Object)
            {
                return ApiJson.Error("invalid_body", "body must be {\"overrides\": {key: value}}", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                var inst = instances.ResolveInstance(@ref);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'");
                }

                var settings = inst.Settings;

                foreach (var property in request.Overrides.EnumerateObject())
                {
                    var setting = settings.GetSetting(property.Name);
                    if (setting is null)
                    {
                        return ApiJson.Error("unknown_setting", $"no registered setting '{property.Name}'", 400);
                    }

                    // value kind follows the current value (bool/int/long/double/string)
                    setting.Set(ConvertByKind(property.Value, setting.Get()));
                }

                return Results.NoContent();
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);
    }

    // ----------------------------------------------------------------- DTO mapping

    public static InstanceSummary Summary(BaseInstance inst, InstanceList instances)
    {
        _ = inst.Settings; // ensure the type-specific settings (and InstanceType) are loaded
        return new InstanceSummary(
            inst.Id(),
            inst.Uuid,
            instances.InodeOf(inst),
            inst.Id(),
            inst.Name(),
            inst.IconKey(),
            instances.GetInstanceGroup(inst.Id()),
            inst.LastLaunch(),
            inst.TotalTimePlayed(),
            inst.IsRunning,
            InstanceType(inst));
    }

    public static InstanceDetail Detail(BaseInstance inst, InstanceList instances)
    {
        _ = inst.Settings;
        return new InstanceDetail(
            inst.Id(),
            inst.Uuid,
            instances.InodeOf(inst),
            inst.Id(),
            inst.Name(),
            inst.IconKey(),
            instances.GetInstanceGroup(inst.Id()),
            inst.LastLaunch(),
            inst.TotalTimePlayed(),
            inst.IsRunning,
            InstanceType(inst),
            inst.Notes(),
            inst.GameRoot.Value);
    }

    private static string InstanceType(BaseInstance inst) =>
        inst is NullInstance ? "null" : inst.InstanceType() is { Length: > 0 } t ? t : "unknown";

    /// <summary>The effective value of a registered setting, converted to the kind the
    /// setting declares through its default (bool/int/long/double/string).</summary>
    private static object? TypedByDefaultKind(Setting setting)
    {
        var value = setting.Get();
        return setting.Default switch
        {
            bool => Variant.AsBool(value),
            int => Variant.AsInt(value),
            long => Variant.AsLong(value),
            double => Variant.AsDouble(value),
            _ => value,
        };
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create((long)i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        IReadOnlyList<string> list => new JsonArray(list.Select(v => JsonValue.Create(v)).ToArray()),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        _ => JsonValue.Create(value.ToString()),
    };

    internal static object? ConvertByKind(JsonElement value, object? current) => current switch
    {
        bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : bool.TryParse(value.ToString(), out var b) && b,
        int => value.ValueKind == JsonValueKind.Number ? value.GetInt32()
            : int.TryParse(value.ToString(), out var i) ? i : 0,
        long => value.ValueKind == JsonValueKind.Number ? value.GetInt64()
            : long.TryParse(value.ToString(), out var l) ? l : 0L,
        double => value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : double.TryParse(value.ToString(), out var d) ? d : 0d,
        // typed string lists (linkedInstances): a JSON array of strings
        IReadOnlyList<string> => value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.ToString())
                .ToList()
            : [],
        _ => value.ToString(),
    };
}
