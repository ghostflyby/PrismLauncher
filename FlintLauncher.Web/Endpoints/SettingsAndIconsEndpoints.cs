// SPDX-License-Identifier: GPL-3.0-only
// Settings groups (the global settings pages of the C++ UI collapse into named key groups
// over the same flint.cfg-backed IniSettingsObject) and the global icon library endpoints
// (IconPickerDialog). UI-API mapping Wave B.
using FlintLauncher.Core.Icons;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using System.Text.Json;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class SettingsAndIconsEndpoints
{
    /// <summary>group -> (key -> typed default) — the keys each C++ settings page owns.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Groups =
        new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["launcher"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["MenuBarInsteadOfToolBar"] = false,
                ["NumberOfConcurrentTasks"] = 10,
                ["NumberOfConcurrentDownloads"] = 6,
                ["NumberOfManualRetries"] = 1,
                ["RequestTimeout"] = 60,
                ["CloseAfterLaunch"] = false,
                ["QuitAfterGameStop"] = false,
                ["AutoCloseConsole"] = false,
            },
            ["minecraft"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["MinMemAlloc"] = 512,
                ["MaxMemAlloc"] = 4096,
                ["PermGen"] = 192,
                ["MinecraftWinWidth"] = 854,
                ["MinecraftWinHeight"] = 480,
                ["LaunchMaximized"] = false,
                ["PreLaunchCommand"] = "",
                ["PostExitCommand"] = "",
                ["WrapperCommand"] = "",
                ["Env"] = "{}",
            },
            ["java"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["JavaPath"] = "java",
                ["JvmArgs"] = "",
                ["IgnoreJavaCompatibility"] = false,
                ["AutomaticJava"] = false,
                ["AutomaticJavaDownload"] = false,
                ["AutomaticJavaSwitch"] = false,
            },
            ["proxy"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ProxyType"] = "None",
                ["ProxyAddr"] = "",
                ["ProxyPort"] = 8080,
                ["ProxyUser"] = "",
                ["ProxyPass"] = "",
            },
            ["api"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["FlameKeyOverride"] = "",
                ["ModrinthToken"] = "",
                ["MetaURLOverride"] = "",
            },
            ["tools"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["JsonEditor"] = "",
                ["JProfilerPath"] = "",
                ["JVisualVMPath"] = "",
                ["MCEditPath"] = "",
            },
        };

    public static void MapSettingsAndIcons(this IEndpointRouteBuilder api)
    {
        api.MapGet("/settings", (LauncherRuntime runtime) =>
        {
            var groups = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (group, keys) in Groups)
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, def) in keys)
                {
                    values[key] = runtime.GlobalSettings.GetSetting(key) is { } setting
                        ? TypedValue(setting.Get(), def)
                        : def;
                }
                groups[group] = values;
            }
            return Results.Ok(new SettingsGroupsResponse(groups));
        });

        api.MapPut("/settings", (LauncherRuntime runtime, GlobalSettingsPutRequest? body) =>
        {
            if (body is null || !Groups.ContainsKey(body.Group ?? ""))
            {
                return ApiJson.Error("invalid_request",
                    $"'group' must be one of: {string.Join(", ", Groups.Keys)}", 400);
            }
            var keys = Groups[body.Group];
            foreach (var prop in body.Values.EnumerateObject())
            {
                if (!keys.TryGetValue(prop.Name, out var def))
                {
                    return ApiJson.Error("unknown_setting",
                        $"key '{prop.Name}' is not part of group '{body.Group}'", 400);
                }
                runtime.GlobalSettings.Set(prop.Name, TypedValue(
                    prop.Value.ValueKind == JsonValueKind.Null ? "" : (object?)prop.Value.Clone(), def));
            }
            return Results.NoContent();
        });

        // ---- global icon library (IconPickerDialog) ------------------------------------

        api.MapGet("/icons", (LauncherRuntime runtime) =>
        {
            var iconsDir = Path.Combine(runtime.DataDir, "icons");
            Directory.CreateDirectory(iconsDir);
            var icons = IconFiles.GetIconFilePaths(iconsDir)
                .Select(file => new IconView(
                    IconFiles.IconKeyForFile(iconsDir, file),
                    IconFiles.IconDisplayName(iconsDir, file)))
                .OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Results.Ok(icons);
        });

        api.MapPost("/icons", async (HttpRequest request, LauncherRuntime runtime) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
            {
                return ApiJson.Error("invalid_request", "multipart file field is required", 400);
            }
            var iconsDir = Path.Combine(runtime.DataDir, "icons");
            Directory.CreateDirectory(iconsDir);
            var installed = new List<string>();
            foreach (var file in request.Form.Files)
            {
                var suffix = Path.GetExtension(file.FileName).TrimStart('.');
                if (!IconFiles.IsIconSuffix(suffix.ToLowerInvariant()))
                {
                    return ApiJson.Error("invalid_icon", $"unsupported icon type: {file.FileName}", 400);
                }
                var temp = Path.Combine(Path.GetTempPath(), $"flint-icon-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
                await using (var stream = File.Create(temp))
                {
                    await file.CopyToAsync(stream);
                }
                var name = Path.GetFileNameWithoutExtension(file.FileName);
                if (!IconFiles.InstallIcon(iconsDir, temp, name))
                {
                    return ApiJson.Error("invalid_icon", $"could not install {file.FileName}", 400);
                }
                installed.Add(name);
            }
            return Results.Ok(new IconInstalledResponse(installed[0]));
        });

        api.MapPut("/instances/{id}/icon", async (string id, HttpRequest request, LauncherRuntime runtime) =>
        {
            if (runtime.Instances.GetInstanceById(id) is not MinecraftInstance instance)
            {
                return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
            }
            string key;
            if (request.HasFormContentType && request.Form.Files.Count > 0)
            {
                var iconsDir = Path.Combine(runtime.DataDir, "icons");
                Directory.CreateDirectory(iconsDir);
                var file = request.Form.Files[0];
                var temp = Path.Combine(Path.GetTempPath(), $"flint-icon-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
                await using (var stream = File.Create(temp))
                {
                    await file.CopyToAsync(stream);
                }
                if (!IconFiles.InstallIcon(iconsDir, temp, instance.Id()))
                {
                    return ApiJson.Error("invalid_icon", $"could not install {file.FileName}", 400);
                }
                key = instance.Id();
            }
            else
            {
                var form = await request.ReadFormAsync();
                key = form["iconKey"].ToString();
                if (key.Length == 0)
                {
                    return ApiJson.Error("invalid_request", "iconKey or multipart file is required", 400);
                }
            }
            instance.SetIconKey(key);
            instance.SaveNow();
            return Results.NoContent();
        });
    }

    /// <summary>Coerce the incoming JSON value to the declared default's kind, like the
    /// instance settings endpoint.</summary>
    private static object TypedValue(object? raw, object? def) => def switch
    {
        bool => Variant.AsBool(raw),
        int => Variant.AsInt(raw),
        long => Variant.AsLong(raw),
        double => Variant.AsDouble(raw),
        _ => Variant.AsString(raw),
    };
}
