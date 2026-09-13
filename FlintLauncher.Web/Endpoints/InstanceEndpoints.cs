// SPDX-License-Identifier: GPL-3.0-only
// Instance endpoints: list/get/create-vanilla/import/delete/patch + instance settings.
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Instances.Creation;
using FlintLauncher.Core.Settings;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class InstanceEndpoints
{
    public static void MapInstances(this IEndpointRouteBuilder api)
    {
        api.MapGet("/instances", async (LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                return Results.Ok(runtime.Instances.Instances.Select(inst => Summary(inst, runtime.Instances)).ToArray());
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapGet("/instances/{id}", async (string id, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                var inst = runtime.Instances.GetInstanceById(id);
                return inst is null
                    ? ApiJson.NotFound("instance_not_found", $"no instance '{id}'")
                    : Results.Ok(Detail(inst, runtime.Instances));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPost("/instances/vanilla", async (VanillaCreateRequest request, LauncherRuntime runtime) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.McVersion))
            {
                return ApiJson.Error("invalid_body", "'name' and 'mcVersion' are required", 400);
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                // staged-commit path: VanillaCreationTask runs inside an InstanceList staging
                // folder; InstanceStaging commits the folder into instances/ on success. The
                // flow is quick and offline-safe (only the component list is written), so the
                // endpoint runs it synchronously and returns the committed instance id.
                var task = new VanillaCreationTask(request.McVersion)
                {
                    TargetDir = "",
                    Group = request.Group ?? "",
                };
                task.SetName(request.Name);
                task.SetIcon(request.IconKey ?? "default");

                var wrapped = runtime.Instances.WrapInstanceTask(task);
                // the commit derives the folder from the display name: compute the expected
                // id BEFORE running the task (after the commit the directory already exists
                // and DirNameFromString would return "<name>(1)")
                var expectedId = FileSystem.DirNameFromString(request.Name, runtime.Instances.PrimaryDir());
                await wrapped.StartAsync();
                if (!wrapped.WasSuccessful)
                {
                    return ApiJson.Error("creation_failed", wrapped.FailReason, 500);
                }

                runtime.Instances.LoadList();
                var inst = runtime.Instances.GetInstanceById(expectedId);
                return inst is null
                    ? ApiJson.Error("creation_failed", "instance was committed but not discovered", 500)
                    : Results.Created($"/api/v1/instances/{inst.Id()}",
                        new InstanceCreatedResponse(inst.Id(), inst.Name()));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPost("/instances/import", async (ImportRequest request, LauncherRuntime runtime,
                                                FlintTaskRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url) ||
                !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "file"))
            {
                return ApiJson.Error("invalid_url", "'url' must be an absolute http(s)/file URL", 400);
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                var importTask = new InstanceImportTask(uri, trustedSource: true)
                {
                    MetaCache = runtime.MetaCache,
                };
                // optional display name: without one the committed folder cannot be derived
                // (the C++ always has the pack name from the import dialog)
                if (!string.IsNullOrWhiteSpace(request.Name))
                {
                    importTask.SetName(request.Name);
                }
                var wrapped = runtime.Instances.WrapInstanceTask(importTask);
                // NOTE: the hook runs via Task.Run — the fire-and-forget StartAsync below can
                // complete synchronously on this thread (small/file packs), and an inline
                // InstanceLock.Wait() here would deadlock against this very handler
                var entry = registry.Register("import", wrapped, onFinished: () => _ = Task.Run(() =>
                {
                    runtime.InstanceLock.Wait();
                    try
                    {
                        runtime.Instances.LoadList();
                    }
                    finally
                    {
                        runtime.InstanceLock.Release();
                    }
                }));
                _ = wrapped.StartAsync(); // 202: downloads/modpack processing run in background
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapDelete("/instances/{id}", async (string id, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                var inst = runtime.Instances.GetInstanceById(id);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{id}'");
                }
                runtime.Instances.DeleteInstance(id);
                return Results.NoContent();
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPatch("/instances/{id}", async (string id, PatchInstanceRequest request, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                var inst = runtime.Instances.GetInstanceById(id);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{id}'");
                }

                // metadata edits first (persisted at the current directory), like upstream's
                // rename flow where the name edit commits before the directory moves
                var renameRequested = false;
                if (request.Name is { } name && name.Length != 0 && name != inst.Name())
                {
                    inst.SetName(name);
                    renameRequested = true;
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
                    runtime.Instances.SetInstanceGroup(inst.Id(), group);
                }
                runtime.Instances.SaveNow();

                if (renameRequested)
                {
                    // syncInstanceDirName semantics: also rename the instance directory (the
                    // id follows the directory name); failure keeps a metadata-only rename
                    var groupBefore = runtime.Instances.GetInstanceGroup(inst.Id());
                    var newRoot = Path.Combine(runtime.Instances.PrimaryDir(),
                        FileSystem.DirNameFromString(request.Name!, runtime.Instances.PrimaryDir()));
                    if (inst.SyncInstanceDirName(newRoot))
                    {
                        runtime.Instances.LoadList();
                        // carry the group over to the new id (upstream re-applies the group
                        // after refreshing the instance list)
                        var finalGroup = request.Group ?? groupBefore;
                        if (finalGroup.Length != 0)
                        {
                            runtime.Instances.SetInstanceGroup(Path.GetFileName(newRoot), finalGroup);
                        }
                    }
                }
                return Results.NoContent();
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        // ------------------------------------------------------------ instance settings
        api.MapGet("/instances/{id}/settings", async (string id, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                var inst = runtime.Instances.GetInstanceById(id);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{id}'");
                }
                var settings = inst.Settings();
                var node = new JsonObject();
                if (settings is IniSettingsObject ini && File.Exists(ini.FilePath))
                {
                    var file = new IniFile();
                    file.LoadFile(ini.FilePath);
                    foreach (var key in file.Keys)
                    {
                        // registered keys are reported with their typed effective value
                        // (typed by the setting's declared default kind — the INI stores
                        // everything as strings); unregistered INI keys stay raw strings
                        // (SettingsObject does not expose setting enumeration, so the INI
                        // keys define the map — documented)
                        node[key] = settings.GetSetting(key) is { } setting
                            ? ToNode(TypedByDefaultKind(setting))
                            : file.Get(key) is { } raw ? ToNode(Variant.FromIniValue(raw)) : null;
                    }
                }
                return ApiJson.Node(node);
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPut("/instances/{id}/settings", async (string id, SettingsPutRequest request,
                                                      LauncherRuntime runtime) =>
        {
            if (request.Overrides.ValueKind != JsonValueKind.Object)
            {
                return ApiJson.Error("invalid_body", "body must be {\"overrides\": {key: value}}", 400);
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                var inst = runtime.Instances.GetInstanceById(id);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{id}'");
                }
                var settings = inst.Settings();

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
                runtime.InstanceLock.Release();
            }
        });
    }

    // ----------------------------------------------------------------- DTO mapping

    public static InstanceSummary Summary(BaseInstance inst, InstanceList list)
    {
        _ = inst.Settings(); // ensure the type-specific settings (and InstanceType) are loaded
        return new InstanceSummary(
            inst.Id(),
            inst.Name(),
            inst.IconKey(),
            list.GetInstanceGroup(inst.Id()),
            inst.LastLaunch(),
            inst.TotalTimePlayed(),
            inst.IsRunning,
            InstanceType(inst));
    }

    public static InstanceDetail Detail(BaseInstance inst, InstanceList list)
    {
        _ = inst.Settings();
        return new InstanceDetail(
            inst.Id(),
            inst.Name(),
            inst.IconKey(),
            list.GetInstanceGroup(inst.Id()),
            inst.LastLaunch(),
            inst.TotalTimePlayed(),
            inst.IsRunning,
            InstanceType(inst),
            inst.Notes(),
            inst.GameRoot());
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
        _ => value.ToString(),
    };
}
