// SPDX-License-Identifier: GPL-3.0-only
// Settings groups (the global settings pages of the C++ UI collapse into named key groups
// over the typed flint.json stack — CONFIG C5/C6; the group KEYS are the wire contract and
// stay, while the values come from the single key registry FlintConfigKeys — the old
// Groups value-table + GlobalSettingsRegistration double bookkeeping retired) and the
// global icon library endpoints (IconPickerDialog). UI-API mapping Wave B; the icon half
// runs on the injected IconPool model since DATA D5 (IconsDir setting honored).
//
// G4 secret rerouting: PUT of ProxyPass/ModrinthToken/FlameKeyOverride writes the
// ISecretStore (C4 — flint.json carries no secret fields); GET serves the stored values so
// the wire contract is unchanged for the host.
using FlintLauncher.Core;
using FlintLauncher.Core.Config;
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
    /// <summary>group -> ordered wire keys — the keys each C++ settings page owns. The
    /// VALUES live once, in the record defaults (FlintConfigKeys / the record property
    /// initializers); this table only fixes membership and order.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Groups =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["launcher"] =
            [
                "MenuBarInsteadOfToolBar",
                "NumberOfConcurrentTasks",
                "NumberOfConcurrentDownloads",
                "NumberOfManualRetries",
                "RequestTimeout",
                "CloseAfterLaunch",
                "QuitAfterGameStop",
                "AutoCloseConsole",
                // LauncherPage (instance list + console behavior)
                "InstSortMode",
                "InstRenamingMode",
                "ShowConsole",
                "ShowConsoleOnError",
                "LogPrePostOutput",
                "ConsoleMaxLines",
                "ConsoleOverflowStop",
                // java wizard / modpack flow switches owned by the launcher pages
                "IgnoreJavaWizard",
                "ModDependenciesDisabled",
                "SkipModpackUpdatePrompt",
                "DownloadGameFilesDuringInstanceCreation",
                "ShowModIncompat",
            ],
            ["minecraft"] =
            [
                "MinMemAlloc",
                "MaxMemAlloc",
                "MinecraftWinWidth",
                "MinecraftWinHeight",
                "LaunchMaximized",
                "PreLaunchCommand",
                "PostExitCommand",
                "WrapperCommand",
                "Env",
                // game time (MinecraftPage "game time" section)
                "ShowGameTime",
                "ShowGlobalGameTime",
                "RecordGameTime",
                "ShowGameTimeWithoutDays",
                // native library workarounds + performance (MinecraftPage "workarounds")
                "UseNativeOpenAL",
                "CustomOpenALPath",
                "UseNativeGLFW",
                "CustomGLFWPath",
                "UseNativeSDL",
                "CustomSDLPath",
                "EnableFeralGamemode",
                "EnableMangoHud",
                "UseDiscreteGpu",
                "UseZink",
                "LowMemWarning",
                "OnlineFixes",
            ],
            ["java"] =
            [
                // C++ default is the empty path: the automatic-java machinery fills it
                "JavaPath",
                "JvmArgs",
                "IgnoreJavaCompatibility",
                // AutomaticJava is the instance-level flag (MinecraftInstance.cpp:191 registers
                // it instance-side; AutoInstallJava writes it, BaseInstance.cpp:201 consults
                // it); AutomaticJavaSwitch/Download are the GLOBAL switches whose C++ defaults
                // derive from JavaPath being empty (Application.cpp:768-770 — the one-shot
                // import materializes the derived pair into flint.json, E3)
                "AutomaticJava",
                "AutomaticJavaDownload",
                "AutomaticJavaSwitch",
            ],
            ["dirs"] =
            [
                // LauncherPage folders (IconsDir/SkinsDir relative to the data dir, JavaDir
                // under STORE root semantics since the E2 remount; InstanceDir itself is
                // structural in the host and stays unwritable). CentralModsDir left with
                // DATA D8 (true dead setting, unregistered).
                "IconsDir",
                "DownloadsDir",
                "DownloadsDirWatchRecursive",
                "MoveModsFromDownloadsDir",
                "SkinsDir",
                "JavaDir",
            ],
            ["language"] =
            [
                // LanguagePage
                "Language",
                "UseSystemLocale",
            ],
            ["meta"] =
            [
                // custom mirrors: MetaURLOverride feeds the metadata index, ResourceURLOverride
                // the AssetUpdateTask and LegacyFMLLibsURLOverride the LegacyFMLLibrariesTask
                // (read hooks already live in the core launch steps)
                "MetaURLOverride",
                "ResourceURLOverride",
                "LegacyFMLLibsURLOverride",
            ],
            ["proxy"] =
            [
                // ProxyPage: written values reach the NetStack through RuntimeNetConfig;
                // ProxyPass is a C4 secret — same wire key, ISecretStore storage
                "ProxyType",
                "ProxyAddr",
                "ProxyPort",
                "ProxyUser",
                "ProxyPass",
            ],
            ["api"] =
            [
                "FlameKeyOverride",
                "ModrinthToken",
                "MetaURLOverride",
                "MSAClientIDOverride",
                "UserAgentOverride",
                // paste providers (the registerSettings "HACK" block; the paste endpoint reads them)
                "PastebinType",
                "PastebinCustomAPIBase",
            ],
            ["tools"] =
            [
                "JsonEditor",
                "JProfilerPath",
                "JVisualVMPath",
                "MCEditPath",
            ],
        };

    public static void MapSettingsAndIcons(this IEndpointRouteBuilder api)
    {
        api.MapGet("/settings", (LauncherRuntime runtime) =>
        {
            var groups = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (group, keys) in Groups)
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    // G4: the secret trio serves from the store; everything else from the
                    // typed stack's resolved fold (defaults = the record property defaults)
                    values[key] = FlintConfigKeys.IsSecret(key)
                        ? runtime.Secrets.Get(key) ?? ""
                        : TypedValue(runtime.Config.Get(key), FlintConfigKeys.Default(key));
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
                if (!keys.Contains(prop.Name))
                {
                    return ApiJson.Error("unknown_setting",
                        $"key '{prop.Name}' is not part of group '{body.Group}'", 400);
                }
                var value = TypedValue(
                    prop.Value.ValueKind == JsonValueKind.Null ? "" : (object?)prop.Value.Clone(),
                    FlintConfigKeys.Default(prop.Name));
                if (FlintConfigKeys.IsSecret(prop.Name))
                {
                    // G4: the secret trio never touches flint.json — the write goes to the
                    // ISecretStore (empty clears the stored secret)
                    if (Variant.AsString(value) is { Length: > 0 } secret)
                    {
                        runtime.Secrets.Set(prop.Name, secret);
                    }
                    else
                    {
                        runtime.Secrets.Delete(prop.Name);
                    }
                }
                else
                {
                    runtime.Config.Set(prop.Name, value);
                }
            }
            // proxy / retries / timeout / user-agent take effect immediately (Application
            // re-runs updateProxySettings from the ProxyPage the same way); the apply also
            // re-reads the store-backed secrets and the mod-platform API credentials
            RuntimeNetConfig.Apply(runtime);
            return Results.NoContent();
        });

        // ---- global icon library (IconPickerDialog) ------------------------------------
        // D5: the surface talks to the injected IconPool model (key→path index, scan,
        // install/delete) and the IconsDir SETTING decides where the library lives — the
        // three former hardcoded Path.Combine(DataDir, "icons") sites ignored the setting.

        api.MapGet("/icons", (IconPool icons) =>
        {
            Directory.CreateDirectory(icons.IconsDir);
            return Results.Ok(icons.Entries()
                .Select(entry => new IconView(entry.Key, entry.DisplayName))
                .ToArray());
        });

        api.MapPost("/icons", async (HttpRequest request, IconPool icons) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
            {
                return ApiJson.Error("invalid_request", "multipart file field is required", 400);
            }
            Directory.CreateDirectory(icons.IconsDir);
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
                if (!icons.Install(temp, name))
                {
                    return ApiJson.Error("invalid_icon", $"could not install {file.FileName}", 400);
                }
                installed.Add(name);
            }
            return Results.Ok(new IconInstalledResponse(installed[0]));
        });

        api.MapDelete("/icons/{key}", (string key, IconPool icons) =>
            icons.Delete(key)
                ? Results.NoContent()
                : ApiJson.NotFound("icon_not_found", $"no icon '{key}'"));

        api.MapPut("/instances/{ref}/icon", async (string @ref, HttpRequest request,
            LauncherRuntime runtime) =>
        {
            if (runtime.Instances.ResolveInstance(@ref) is not MinecraftInstance instance)
            {
                return ApiJson.NotFound("instance_not_found", $"no launchable instance '{@ref}'");
            }
            string key;
            if (request.HasFormContentType && request.Form.Files.Count > 0)
            {
                Directory.CreateDirectory(runtime.Icons.IconsDir);
                var file = request.Form.Files[0];
                var temp = Path.Combine(Path.GetTempPath(), $"flint-icon-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
                await using (var stream = File.Create(temp))
                {
                    await file.CopyToAsync(stream);
                }
                if (!runtime.Icons.Install(temp, instance.Id()))
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
