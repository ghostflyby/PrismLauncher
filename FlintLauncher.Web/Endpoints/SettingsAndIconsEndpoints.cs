// SPDX-License-Identifier: GPL-3.0-only
// Settings groups (the global settings pages of the C++ UI collapse into named key groups
// over the same flint.cfg-backed IniSettingsObject) and the global icon library endpoints
// (IconPickerDialog). UI-API mapping Wave B.
using FlintLauncher.Core;
using FlintLauncher.Core.Icons;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using System.Text.Json;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class SettingsAndIconsEndpoints
{
    /// <summary>group -> (key -> typed default) — the keys each C++ settings page owns.
    /// Defaults mirror Application::registerSettings (fix-3: the table and
    /// GlobalSettingsRegistration carry the same values; GET prefers the registered value,
    /// the table supplies the type and the pre-registration fallback).</summary>
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
                // LauncherPage (instance list + console behavior)
                ["InstSortMode"] = "Name",
                ["InstRenamingMode"] = "AskEverytime",
                ["ShowConsole"] = false,
                ["ShowConsoleOnError"] = true,
                ["LogPrePostOutput"] = true,
                ["ConsoleMaxLines"] = 100000,
                ["ConsoleOverflowStop"] = true,
                // java wizard / modpack flow switches owned by the launcher pages
                ["IgnoreJavaWizard"] = false,
                ["ModDependenciesDisabled"] = false,
                ["SkipModpackUpdatePrompt"] = false,
                ["DownloadGameFilesDuringInstanceCreation"] = true,
                ["ShowModIncompat"] = false,
            },
            ["minecraft"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["MinMemAlloc"] = 512,
                ["MaxMemAlloc"] = GlobalSettingsRegistration.DefaultMaxMemAlloc,
                ["PermGen"] = 128,
                ["MinecraftWinWidth"] = 854,
                ["MinecraftWinHeight"] = 480,
                ["LaunchMaximized"] = false,
                ["PreLaunchCommand"] = "",
                ["PostExitCommand"] = "",
                ["WrapperCommand"] = "",
                ["Env"] = "{}",
                // game time (MinecraftPage "game time" section)
                ["ShowGameTime"] = true,
                ["ShowGlobalGameTime"] = true,
                ["RecordGameTime"] = true,
                ["ShowGameTimeWithoutDays"] = false,
                // native library workarounds + performance (MinecraftPage "workarounds")
                ["UseNativeOpenAL"] = false,
                ["CustomOpenALPath"] = "",
                ["UseNativeGLFW"] = false,
                ["CustomGLFWPath"] = "",
                ["UseNativeSDL"] = false,
                ["CustomSDLPath"] = "",
                ["EnableFeralGamemode"] = false,
                ["EnableMangoHud"] = false,
                ["UseDiscreteGpu"] = false,
                ["UseZink"] = false,
                ["LowMemWarning"] = true,
                ["OnlineFixes"] = false,
            },
            ["java"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // C++ default is the empty path: the automatic-java machinery fills it
                ["JavaPath"] = "",
                ["JvmArgs"] = "",
                ["IgnoreJavaCompatibility"] = false,
                // legacy table key kept writable (AutoInstallJava writes it on the instance
                // settings; the C++ derive their defaults from JavaPath being empty)
                ["AutomaticJava"] = true,
                ["AutomaticJavaDownload"] = true,
                ["AutomaticJavaSwitch"] = true,
            },
            ["dirs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // LauncherPage folders (relative to the data dir like the C++ defaults;
                // InstanceDir itself is structural in the host and stays unwritable)
                ["CentralModsDir"] = "mods",
                ["IconsDir"] = "icons",
                ["DownloadsDir"] = GlobalSettingsRegistration.DefaultDownloadsDir,
                ["DownloadsDirWatchRecursive"] = false,
                ["MoveModsFromDownloadsDir"] = false,
                ["SkinsDir"] = "skins",
                ["JavaDir"] = "java",
            },
            ["language"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // LanguagePage
                ["Language"] = "",
                ["UseSystemLocale"] = false,
            },
            ["meta"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // custom mirrors: MetaURLOverride feeds the metadata index, ResourceURLOverride
                // the AssetUpdateTask and LegacyFMLLibsURLOverride the LegacyFMLLibrariesTask
                // (read hooks already live in the core launch steps)
                ["MetaURLOverride"] = "",
                ["ResourceURLOverride"] = "",
                ["LegacyFMLLibsURLOverride"] = "",
            },
            ["proxy"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // ProxyPage: written values reach Http.Configure through RuntimeNetConfig
                ["ProxyType"] = "None",
                ["ProxyAddr"] = "127.0.0.1",
                ["ProxyPort"] = 8080,
                ["ProxyUser"] = "",
                ["ProxyPass"] = "",
            },
            ["api"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["FlameKeyOverride"] = "",
                ["ModrinthToken"] = "",
                ["MetaURLOverride"] = "",
                ["MSAClientIDOverride"] = "",
                ["UserAgentOverride"] = "",
                // paste providers (the registerSettings "HACK" block; the paste endpoint reads them)
                ["PastebinType"] = (int)PasteUpload.PasteType.Mclogs,
                ["PastebinCustomAPIBase"] = "",
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
            if (body is null || body.Group is null || !Groups.ContainsKey(body.Group))
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
            // proxy / retries / timeout / user-agent take effect immediately (Application
            // re-runs updateProxySettings from the ProxyPage the same way)
            RuntimeNetConfig.Apply(runtime);
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
    /// instance settings endpoint. The incoming value is a boxed JsonElement (the body's
    /// raw values); unwrap it to managed primitives first, otherwise it would fall through
    /// every Variant pattern (numbers became 0, booleans became false).</summary>
    private static object TypedValue(object? raw, object? def)
    {
        var value = UnwrapJson(raw);
        return def switch
        {
            bool => Variant.AsBool(value),
            int => Variant.AsInt(value),
            long => Variant.AsLong(value),
            double => Variant.AsDouble(value),
            _ => Variant.AsString(value),
        };
    }

    private static object? UnwrapJson(object? raw)
    {
        if (raw is not JsonElement element)
        {
            return raw;
        }
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => "",
            _ => element.ToString(),
        };
    }
}
