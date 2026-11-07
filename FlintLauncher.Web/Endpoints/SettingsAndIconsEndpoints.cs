// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Settings groups (the global settings pages of the C++ UI collapse into named key groups
// over the typed flint.json stack; the group KEYS are the wire contract and
// stay, while the values come from the single key registry FlintConfigKeys — the old
// Groups value-table + GlobalSettingsRegistration double bookkeeping retired) and the
// global icon library endpoints (IconPickerDialog). The icon half
// runs on the injected IconPool model (IconsDir setting honored).
//
// Secret rerouting: PUT of ProxyPass/ModrinthToken/FlameKeyOverride writes the
// ISecretStore (flint.json carries no secret fields); GET serves the stored values so
// the wire contract is unchanged for the host.

using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Icons;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Settings;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.OpenApi;
using FlintLauncher.Web.Services;
using TruePath;
using TruePath.SystemIo;

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
                // import materializes the derived pair into flint.json)
                "AutomaticJava",
                "AutomaticJavaDownload",
                "AutomaticJavaSwitch",
            ],
            ["dirs"] =
            [
                // LauncherPage folders (IconsDir/SkinsDir relative to the data dir, JavaDir
                // under store root semantics since the store remount; InstanceDir itself is
                // structural in the host and stays unwritable). CentralModsDir left with
                // (true dead setting, unregistered).
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
                // ProxyPage: written values reach the client's handlers through NetConfigApplier;
                // ProxyPass is a secret — same wire key, ISecretStore storage
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

    public static void MapSettingsAndIcons(this RouteGroupBuilder api)
    {
        api.WithTags("Settings");
        api.MapGet("/settings", (FlintConfigStore config, ISecretStore secrets) =>
        {
            var groups = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (group, keys) in Groups)
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    // the secret trio serves from the store; everything else from the
                    // typed stack's resolved fold (defaults = the record property defaults)
                    values[key] = FlintConfigKeys.IsSecret(key)
                        ? secrets.Get(key) ?? ""
                        : TypedValue(config.Get(key), FlintConfigKeys.Default(key));
                }

                groups[group] = values;
            }

            return Results.Ok(new SettingsGroupsResponse(groups));
        }).Produces<SettingsGroupsResponse>();

        api.MapPut("/settings", (FlintConfigStore config, ISecretStore secrets,
            NetConfigApplier netConfig, DirSettingsApplier dirSettings, GlobalSettingsPutRequest? body) =>
        {
            if (body is null || !Groups.TryGetValue(body.Group, out var keys))
            {
                return ApiJson.Error("invalid_request",
                    $"'group' must be one of: {string.Join(", ", Groups.Keys)}", 400);
            }

            // the whole request is checked before anything is written: one unknown key
            // refuses all of it
            foreach (var prop in body.Values.EnumerateObject())
            {
                if (!keys.Contains(prop.Name))
                {
                    return ApiJson.Error("unknown_setting",
                        $"key '{prop.Name}' is not part of group '{body.Group}'", 400);
                }
            }

            var writes = new List<KeyValuePair<string, object?>>();
            var secretWritten = false;
            foreach (var prop in body.Values.EnumerateObject())
            {
                var value = TypedValue(
                    prop.Value.ValueKind == JsonValueKind.Null ? "" : prop.Value.Clone(),
                    FlintConfigKeys.Default(prop.Name));
                if (FlintConfigKeys.IsSecret(prop.Name))
                {
                    // the secret trio never touches flint.json — the write goes to the
                    // ISecretStore (empty clears the stored secret)
                    if (Variant.AsString(value) is { Length: > 0 } secret)
                    {
                        secrets.Set(prop.Name, secret);
                    }
                    else
                    {
                        secrets.Delete(prop.Name);
                    }

                    secretWritten = true;
                }
                else
                {
                    writes.Add(new(prop.Name, value));
                }
            }

            // one commit for the group: one atomic save, one change notification — the
            // host's subscription re-applies the network stack and the dir-kind models from
            // it, so a listener never sees a half-written proxy tuple
            config.Set(writes);

            // a secret write replaces no config snapshot, so nothing fires for it: the
            // secret-backed values (proxy password, API credentials) are applied here
            if (secretWritten)
            {
                netConfig.Apply();
            }

            // the dir-kind models re-pointed through the subscription already; this second,
            // idempotent pass is the one whose failure reaches the caller (a root that cannot
            // be created is a 500, not a log line)
            if (body.Group == "dirs")
            {
                dirSettings.Apply();
            }

            return Results.NoContent();
        }).Produces(204);

        // ---- global icon library (IconPickerDialog) ------------------------------------
        // the surface talks to the injected IconPool model (key→path index, scan,
        // install/delete) and the IconsDir SETTING decides where the library lives — the
        // three former hardcoded Path.Combine(DataDir, "icons") sites ignored the setting.

        api.MapGet("/icons", (IconPool icons) =>
        {
            icons.IconsDir.CreateDirectory();
            return Results.Ok(icons.Entries()
                .Select(entry => new IconView(entry.Key, entry.DisplayName))
                .ToArray());
        }).Produces<IconView[]>();

        api.MapPost("/icons", async (HttpRequest request, IconPool icons) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
            {
                return ApiJson.Error("invalid_request", "multipart file field is required", 400);
            }

            icons.IconsDir.CreateDirectory();
            var installed = new List<string>();
            foreach (var file in request.Form.Files)
            {
                var suffix = Path.GetExtension(file.FileName).TrimStart('.');
                if (!IconFiles.IsIconSuffix(suffix.ToLowerInvariant()))
                {
                    return ApiJson.Error("invalid_icon", $"unsupported icon type: {file.FileName}", 400);
                }

                var temp = new AbsolutePath(Path.GetTempPath())
                           / $"flint-icon-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}";
                await using (var stream = temp.Create())
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
        }).WithMetadata(new FormUploadMarker("multipart form: icon file (png/jpg/svg)")).Produces<IconInstalledResponse>();

        api.MapDelete("/icons/{key}", (string key, IconPool icons) =>
            icons.Delete(key)
                ? Results.NoContent()
                : ApiJson.NotFound("icon_not_found", $"no icon '{key}'")).Produces(204);

        api.MapPut("/instances/{ref}/icon", async (string @ref, HttpRequest request,
            InstanceList instances, IconPool icons) =>
        {
            if (instances.ResolveInstance(@ref) is not MinecraftInstance instance)
            {
                return ApiJson.NotFound("instance_not_found", $"no launchable instance '{@ref}'");
            }

            string key;
            if (request is { HasFormContentType: true, Form.Files.Count: > 0 })
            {
                icons.IconsDir.CreateDirectory();
                var file = request.Form.Files[0];
                var temp = new AbsolutePath(Path.GetTempPath())
                           / $"flint-icon-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}";
                await using (var stream = temp.Create())
                {
                    await file.CopyToAsync(stream);
                }

                if (!icons.Install(temp, instance.Id()))
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
        }).WithMetadata(new FormUploadMarker("multipart form: icon file, or form field iconKey referencing an installed icon")).Produces(204);
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
