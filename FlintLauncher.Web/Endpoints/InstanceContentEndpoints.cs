// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Instance-content endpoints: the per-instance functional clusters
// of the Qt pages — WorldListPage (worlds), ServersPage (servers.dat CRUD + status ping),
// the external-resource pages (resources/{type}), ScreenshotsPage (listing + imgur upload)
// and GameOptionsPage (options.txt). Long-running flows (imgur upload) run as registered
// tasks (202) like import/mods-install; everything else is synchronous local file work.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.GameOptions;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Minecraft.Worlds;
using FlintLauncher.Core.Screenshots;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.OpenApi;
using FlintLauncher.Web.Services;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Web.Endpoints;

public static class InstanceContentEndpoints
{
    public static void MapInstanceContent(this RouteGroupBuilder api)
    {
        api.WithTags("InstanceContent");
        MapWorlds(api);
        MapServers(api);
        MapResources(api);
        MapMods(api);
        MapScreenshots(api);
        MapGameOptions(api);
    }

    // ------------------------------------------------------------------------ worlds

    private static void MapWorlds(IEndpointRouteBuilder api)
    {
        api.MapGet("/instances/{id}/worlds", async (string id, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                // a fresh WorldList per request (the C++ page model lives for the page)
                var list = instance.WorldList();
                list.Update();
                await list.LoadWorldsAsync();
                return Results.Ok(list.Worlds.Select(ToWorldView).ToArray());
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<WorldView[]>();

        api.MapDelete("/instances/{id}/worlds/{folderName}", async (string id, string folderName,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var list = instance.WorldList();
                list.Update();
                var index = IndexOfFolder(list, folderName);
                if (index < 0)
                {
                    return ApiJson.NotFound("world_not_found", $"no world '{folderName}'");
                }

                var ok = await list.DeleteWorldAsync(index);
                return ok
                    ? Results.NoContent()
                    : ApiJson.Error("world_delete_failed",
                        $"world '{folderName}' could not be deleted", 500);
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);

        // world detail: the listing view plus the parsed seed and the icon.png path
        // (WorldPage's info pane / "reset icon" target)
        api.MapGet("/instances/{id}/worlds/{folderName}", async (string id, string folderName,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var list = instance.WorldList();
                list.Update();
                var index = IndexOfFolder(list, folderName);
                if (index < 0)
                {
                    return ApiJson.NotFound("world_not_found", $"no world '{folderName}'");
                }

                var world = list[index];
                world.LoadMetadata();
                world.SetSize(WorldList.CalculateWorldSize(world.ContainerPath));
                return Results.Ok(new WorldDetailView(
                    world.Name,
                    world.FolderName,
                    world.LastPlayed.ToUnixTimeMilliseconds(),
                    world.GameType.ToLogString(),
                    world.ContainerPath,
                    world.Size,
                    world.Seed,
                    world.IconFile));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<WorldDetailView>();

        // world copy (WorldPage's "copy" action -> CopyWorldTask): the copy lands in the same
        // saves folder under the given name (folder rename + level.dat LevelName rewrite)
        api.MapPost("/instances/{id}/worlds/{folderName}/copy", async (string id, string folderName,
            WorldCopyRequest request, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiJson.Error("invalid_body", "'name' is required", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var list = instance.WorldList();
                list.Update();
                var index = IndexOfFolder(list, folderName);
                if (index < 0)
                {
                    return ApiJson.NotFound("world_not_found", $"no world '{folderName}'");
                }

                if (!await list.CopyWorldAsync(index, request.Name))
                {
                    return ApiJson.Error("world_copy_failed",
                        $"world '{folderName}' could not be copied", 500);
                }

                // CopyWorldAsync refreshed the list; the copy carries the new name both in
                // level.dat (World::rename rewrote it) and as its (sanitized) folder name
                var copy = list.Worlds.FirstOrDefault(w =>
                    string.Equals(w.Name, request.Name, StringComparison.Ordinal) ||
                    string.Equals(w.FolderName, request.Name, StringComparison.Ordinal));
                return copy is null
                    ? Results.NoContent()
                    : Results.Created($"/api/v1/instances/{Uri.EscapeDataString(id)}/worlds/{Uri.EscapeDataString(copy.FolderName)}",
                        ToWorldView(copy));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<WorldView>(201);

        api.MapPost("/instances/{id}/worlds/{folderName}/rename", async (string id, string folderName,
            WorldRenameRequest request, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            if (string.IsNullOrWhiteSpace(request.NewName))
            {
                return ApiJson.Error("invalid_body", "'newName' is required", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var list = instance.WorldList();
                list.Update();
                var index = IndexOfFolder(list, folderName);
                if (index < 0)
                {
                    return ApiJson.NotFound("world_not_found", $"no world '{folderName}'");
                }

                var world = list[index];
                world.LoadMetadata();
                if (!world.Rename(request.NewName))
                {
                    return ApiJson.Error("world_rename_failed",
                        $"world '{folderName}' could not be renamed", 500);
                }

                return Results.NoContent();
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);

        // multipart zip upload (WorldListPage's "add world" drag & drop / file dialog)
        api.MapPost("/instances/{id}/worlds/install", async (string id, HttpRequest request,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return ApiJson.Error("invalid_body", "a multipart 'file' part with a world zip is required", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var list = instance.WorldList();
                list.Update();
                var before = list.Worlds.Select(w => w.FolderName).ToHashSet();

                // stage the upload under its original extension (World identifies zip worlds
                // by the .zip extension)
                var extension = Path.GetExtension(file.FileName);
                if (extension.Length == 0)
                {
                    extension = ".zip";
                }

                var tempPath = Path.Combine(Path.GetTempPath(),
                    $"flint-world-upload-{Guid.NewGuid():N}{extension}");
                await using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                try
                {
                    if (!await list.InstallWorldAsync(tempPath))
                    {
                        return ApiJson.Error("world_install_failed",
                            "the uploaded file is not a valid world zip", 400);
                    }
                }
                catch (Exception e) when (e is InvalidDataException or IOException
                                              or UnauthorizedAccessException or NotSupportedException)
                {
                    // a corrupt archive blows up inside the zip reader, not the validity check
                    return ApiJson.Error("world_install_failed",
                        $"the uploaded file is not a valid world zip: {e.Message}", 400);
                }
                finally
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException) { }
                }

                // report the world that appeared (folder name derives from the level.dat
                // name); the metadata fill re-runs because Update() swaps in fresh objects
                list.Update();
                await list.LoadWorldsAsync();
                var added = list.Worlds.FirstOrDefault(w => !before.Contains(w.FolderName));
                return added is null
                    ? Results.NoContent()
                    : Results.Created($"/api/v1/instances/{Uri.EscapeDataString(id)}/worlds",
                        ToWorldView(added));
            }
            finally
            {
                instanceLock.Release();
            }
        }).WithMetadata(new FormUploadMarker("multipart form: world archive file (zip)")).Produces<WorldView>(201);
    }

    // ----------------------------------------------------------------------- servers

    private static void MapServers(IEndpointRouteBuilder api)
    {
        api.MapGet("/instances/{id}/servers", async (string id, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                return Results.Ok(LoadServers(instance)
                    .Select((entry, index) => ToServerView(index, entry))
                    .ToArray());
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<ServerEntryView[]>();

        api.MapPost("/instances/{id}/servers", async (string id, ServerAddRequest request,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            if (string.IsNullOrWhiteSpace(request.Address))
            {
                return ApiJson.Error("invalid_body", "'address' is required", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var servers = LoadServers(instance);
                var entry = new ServerEntry(
                    string.IsNullOrWhiteSpace(request.Name) ? "Minecraft Server" : request.Name,
                    request.Address.Trim());
                servers.Add(entry);
                ServersDat.Save(ServersPath(instance), servers);
                return Results.Created(
                    $"/api/v1/instances/{Uri.EscapeDataString(id)}/servers/{servers.Count - 1}",
                    ToServerView(servers.Count - 1, entry));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<ServerEntryView>(201);

        api.MapDelete("/instances/{id}/servers/{index}", async (string id, int index,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var servers = LoadServers(instance);
                if (index < 0 || index >= servers.Count)
                {
                    return ApiJson.NotFound("server_not_found", $"no server at index {index}");
                }

                servers.RemoveAt(index);
                ServersDat.Save(ServersPath(instance), servers);
                return Results.NoContent();
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);

        // status ping (ServersPage's queryServersStatus): SRV-resolve then the minimal
        // McClient handshake; synchronous with a 5s bound, like the page's single query
        api.MapPost("/instances/{id}/servers/{index}/ping", async (string id, int index,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var servers = LoadServers(instance);
                if (index < 0 || index >= servers.Count)
                {
                    return ApiJson.NotFound("server_not_found", $"no server at index {index}");
                }

                try
                {
                    var result = await ServerStatusPing.PingAsync(servers[index].Address);
                    return Results.Ok(new ServerPingView(servers[index].Address, result.ResolvedPort,
                        result.ResolvedIp, result.ResolvedPort, result.OnlinePlayers, result.MaxPlayers,
                        result.Version, result.Motd, result.LatencyMs));
                }
                catch (InvalidOperationException e)
                {
                    return ApiJson.Error("ping_failed", e.Message, 502);
                }
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<ServerPingView>();
    }

    // --------------------------------------------------------------------- resources

    private static void MapResources(IEndpointRouteBuilder api)
    {
        // NOTE: the resources family EXCLUDES mods — the metadata-rich
        // /instances/{id}/mods surface below is the single mods source of truth.
        api.MapGet("/instances/{id}/resources/{type}", async (string id, string type,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                if (type == "mods")
                {
                    return ApiJson.Error("use_mods_endpoint",
                        "mods are served by /instances/{id}/mods; the resources endpoints do not list them", 400);
                }

                if (ResourceFolder(instance, type) is not { } folder)
                {
                    return ApiJson.Error("unknown_type",
                        "type must be one of resourcepacks|texturepacks|shaderpacks|datapacks", 400);
                }

                folder.Update();
                return Results.Ok(folder.Resources.Select(ToResourceView).ToArray());
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<ResourceView[]>();

        api.MapPost("/instances/{id}/resources/{type}/{name}/enable", async (string id, string type,
                string name, InstanceList instances, InstanceMutationLock instanceLock) =>
            await SetResourceEnabled(id, type, name, EnableAction.Enable, instances, instanceLock)).Produces(204);

        api.MapPost("/instances/{id}/resources/{type}/{name}/disable", async (string id, string type,
                string name, InstanceList instances, InstanceMutationLock instanceLock) =>
            await SetResourceEnabled(id, type, name, EnableAction.Disable, instances, instanceLock)).Produces(204);

        api.MapDelete("/instances/{id}/resources/{type}/{name}", async (string id, string type,
            string name, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                if (type == "mods")
                {
                    return ApiJson.Error("use_mods_endpoint",
                        "mods are served by /instances/{id}/mods; the resources endpoints do not uninstall them", 400);
                }

                if (ResourceFolder(instance, type) is not { } folder)
                {
                    return ApiJson.Error("unknown_type",
                        "type must be one of resourcepacks|texturepacks|shaderpacks|datapacks", 400);
                }

                folder.Update();
                // UninstallResource resolves the ".disabled" suffixed file from the bare name
                return folder.UninstallResource(name)
                    ? Results.NoContent()
                    : ApiJson.NotFound("resource_not_found", $"no resource '{name}' in '{type}'");
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);
    }

    private static async Task<IResult> SetResourceEnabled(string id, string type, string name,
        EnableAction action, InstanceList instances, InstanceMutationLock instanceLock)
    {
        await instanceLock.WaitAsync();
        try
        {
            if (LookupInstance(instances, id) is not { } instance)
            {
                return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
            }

            if (type == "mods")
            {
                return ApiJson.Error("use_mods_endpoint",
                    "mods are served by /instances/{id}/mods; the resources endpoints do not toggle them", 400);
            }

            if (ResourceFolder(instance, type) is not { } folder)
            {
                return ApiJson.Error("unknown_type",
                    "type must be one of resourcepacks|texturepacks|shaderpacks|datapacks", 400);
            }

            folder.Update();
            var resource = FindResource(folder, name);
            if (resource is null)
            {
                return ApiJson.NotFound("resource_not_found", $"no resource '{name}' in '{type}'");
            }

            // Resource.Enable performs the ".disabled" suffix rename on disk
            var verb = action == EnableAction.Enable ? "enabled" : "disabled";
            if (!resource.Enable(action))
            {
                return ApiJson.Conflict("no_change",
                    $"resource '{name}' was not {verb} (already {verb}, or its type cannot be toggled)");
            }

            folder.Update();
            return Results.NoContent();
        }
        finally
        {
            instanceLock.Release();
        }
    }

    // ------------------------------------------------------------------- mods (metadata-rich)

    /// <summary>The /instances/{id}/mods mutations (enable/disable/uninstall so
    /// the mods surface is fully featured without going through the resources family). The
    /// folder is the instance's cached ModFolderModel — the metadata-aware one, so the
    /// uninstall also drops the packwiz index entry.</summary>
    private static void MapMods(IEndpointRouteBuilder api)
    {
        api.MapPost("/instances/{id}/mods/{name}/enable", async (string id, string name,
                InstanceList instances, InstanceMutationLock instanceLock) =>
            await SetModEnabled(id, name, EnableAction.Enable, instances, instanceLock)).Produces(204);

        api.MapPost("/instances/{id}/mods/{name}/disable", async (string id, string name,
                InstanceList instances, InstanceMutationLock instanceLock) =>
            await SetModEnabled(id, name, EnableAction.Disable, instances, instanceLock)).Produces(204);

        api.MapDelete("/instances/{id}/mods/{name}", async (string id, string name,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var folder = instance.LoaderModList();
                folder.Update();
                return folder.UninstallResource(name)
                    ? Results.NoContent() // the ModFolderModel override also drops the metadata
                    : ApiJson.NotFound("mod_not_found", $"no mod '{name}'");
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);
    }

    private static async Task<IResult> SetModEnabled(string id, string name, EnableAction action,
        InstanceList instances, InstanceMutationLock instanceLock)
    {
        await instanceLock.WaitAsync();
        try
        {
            if (LookupInstance(instances, id) is not { } instance)
            {
                return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
            }

            var folder = instance.LoaderModList();
            folder.Update();
            var mod = FindResource(folder, name);
            if (mod is null)
            {
                return ApiJson.NotFound("mod_not_found", $"no mod '{name}'");
            }

            // Resource.Enable performs the ".disabled" suffix rename on disk (same rename
            // semantics as the resources family)
            var verb = action == EnableAction.Enable ? "enabled" : "disabled";
            if (!mod.Enable(action))
            {
                return ApiJson.Conflict("no_change", $"mod '{name}' was not {verb} (already {verb})");
            }

            folder.Update();
            return Results.NoContent();
        }
        finally
        {
            instanceLock.Release();
        }
    }

    // -------------------------------------------------------------------- screenshots

    private static void MapScreenshots(IEndpointRouteBuilder api)
    {
        api.MapGet("/instances/{id}/screenshots", async (string id, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                return Results.Ok(ListScreenshots(instance).ToArray());
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<ScreenshotView[]>();

        // ScreenshotsPage's imgur upload: each named file uploads as an image; more than one
        // also creates an album (the C++ SequentialTask shape)
        api.MapPost("/instances/{id}/screenshots/upload", async (string id,
            ScreenshotUploadRequest? request, InstanceList instances, InstanceMutationLock instanceLock,
            ImgurApi imgur, FlintTaskRegistry registry) =>
        {
            if (request?.Names is null || request.Names.Count == 0)
            {
                return ApiJson.Error("invalid_body", "body must be {\"names\": [file, ...]}", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var dir = ScreenshotsDir(instance);
                var shots = new List<ScreenShot>();
                foreach (var name in request.Names)
                {
                    var path = ResolveScreenshotPath(dir, name);
                    if (path is not { } some)
                    {
                        return ApiJson.NotFound("screenshot_not_found", $"no screenshot '{name}'");
                    }

                    shots.Add(new ScreenShot(some));
                }

                // test/hermetic hook: the imgur endpoint + client id are overridable like the
                // news feed URL
                var baseUrl = Environment.GetEnvironmentVariable("FLINT_IMGUR_BASE_URL");
                var clientId = Environment.GetEnvironmentVariable("FLINT_IMGUR_CLIENT_ID");

                // ImgurAPI::makeUpload per file; more than one also runs ImgurAPI::makeAlbum.
                // Sequential by design: the album needs the delete hashes the uploads leave.
                var uploads = shots.Select(shot => imgur.MakeUpload(shot, baseUrl, clientId)).ToArray();
                var album = shots.Count > 1
                    ? imgur.MakeAlbum(new ImgurApi.AlbumResult(), shots, baseUrl, clientId)
                    : null;

                var entry = registry.Start("screenshots-upload", handle => handle.RunAsync(async ct =>
                {
                    for (var i = 0; i < uploads.Length; i++)
                    {
                        handle.SetProgress(i, uploads.Length + (album is null ? 0 : 1));
                        await uploads[i].UploadAsync(ct).ConfigureAwait(false);
                    }

                    if (album is not null)
                    {
                        handle.SetProgress(uploads.Length, uploads.Length + 1);
                        await album.CreateAsync(ct).ConfigureAwait(false);
                    }
                }), instance.Id());

                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<TaskAcceptedResponse>(202);

        api.MapDelete("/instances/{id}/screenshots/{name}", async (string id, string name,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var path = ResolveScreenshotPath(ScreenshotsDir(instance), name);
                if (path is not { } exists)
                {
                    return ApiJson.NotFound("screenshot_not_found", $"no screenshot '{name}'");
                }

                try
                {
                    exists.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return ApiJson.Error("screenshot_delete_failed",
                        $"screenshot '{name}' could not be deleted", 500);
                }

                return Results.NoContent();
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);
    }

    // ------------------------------------------------------------------- game options

    private static void MapGameOptions(IEndpointRouteBuilder api)
    {
        api.MapGet("/instances/{id}/gameoptions", async (string id, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var options = new GameOptions((instance.GameRoot / "options.txt"));
                return Results.Ok(options.Contents.Select(item =>
                    new GameOptionView(item.Key, item.Value)).ToArray());
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<GameOptionView[]>();

        api.MapPut("/instances/{id}/gameoptions", async (string id, GameOptionsPutRequest? request,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            if (request?.Options is null || request.Options.Count == 0)
            {
                return ApiJson.Error("invalid_body", "body must be {\"options\": [{key, value}]}", 400);
            }

            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                // the save target is <gameRoot>/options.txt; the game root may not exist
                // yet on a fresh instance (the C++ game folder appears on first launch)
                var gameRoot = instance.GameRoot;
                gameRoot.CreateDirectory();
                var options = new GameOptions(gameRoot / "options.txt");
                foreach (var option in request.Options)
                {
                    options.Set(option.Key, option.Value);
                }

                return options.Save()
                    ? Results.NoContent()
                    : ApiJson.Error("gameoptions_save_failed", "options.txt could not be written", 500);
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces(204);
    }

    // ----------------------------------------------------------------------- helpers

    /// <summary>The MinecraftInstance for a {ref} (the content surfaces need the game
    /// root). Every endpoint resolves through the addressing chain — direct
    /// GetInstanceById is banned at the API boundary.</summary>
    private static MinecraftInstance? LookupInstance(InstanceList instances, string reference) =>
        instances.ResolveInstance(reference) as MinecraftInstance;

    private static AbsolutePath ServersPath(MinecraftInstance instance) =>
        (instance.GameRoot / "servers.dat");

    /// <summary>servers.dat of the game root; a missing file is an empty list (like the C++
    /// model, which starts empty and saves on the first change).</summary>
    private static List<ServerEntry> LoadServers(MinecraftInstance instance) =>
        ServersDat.Load(ServersPath(instance));

    private static ServerEntryView ToServerView(int index, ServerEntry entry) => new(
        index,
        entry.Name,
        entry.Address,
        entry.AcceptsTextures switch
        {
            AcceptsTextures.Always => "always",
            AcceptsTextures.Never => "never",
            _ => "ask",
        },
        entry.Icon is { Length: > 0 });

    private static WorldView ToWorldView(World world) => new(
        world.Name,
        world.FolderName,
        world.LastPlayed.ToUnixTimeMilliseconds(),
        world.GameType.ToLogString(),
        world.ContainerPath,
        world.Size);

    private static int IndexOfFolder(WorldList list, string folderName)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].FolderName, folderName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The ResourceFolderModel for a type key. mods reuse the instance's cached
    /// ModFolderModel (metadata-aware); the other folders are plain ResourceFolderModel
    /// instances over their directories (the port has no dedicated folder models).</summary>
    private static ResourceFolderModel? ResourceFolder(MinecraftInstance instance, string type) =>
        type switch
        {
            "mods" => instance.LoaderModList(),
            "resourcepacks" => new ResourceFolderModel(
                MaterializedDirectory.Ensure(instance.ResourcePacksDir)!, isIndexed: false),
            "texturepacks" => new ResourceFolderModel(
                MaterializedDirectory.Ensure(instance.TexturePacksDir)!, isIndexed: false),
            "shaderpacks" => new ResourceFolderModel(
                MaterializedDirectory.Ensure(instance.ShaderPacksDir)!, isIndexed: false),
            "datapacks" => new ResourceFolderModel(
                MaterializedDirectory.Ensure(instance.GameRoot / "datapacks")!, isIndexed: false),
            _ => null,
        };

    /// <summary>A resource by original file name (with or without the ".disabled" suffix).</summary>
    private static Resource? FindResource(ResourceFolderModel folder, string name)
    {
        foreach (var resource in folder.Resources)
        {
            if (resource.GetOriginalFileName() == name || resource.Fileinfo.Name == name)
            {
                return resource;
            }
        }

        return null;
    }

    private static ResourceView ToResourceView(Resource resource) => new(
        resource.Fileinfo.Name,
        resource.Name,
        resource.Enabled,
        resource.Type switch
        {
            ResourceType.ZipFile => "zip",
            ResourceType.SingleFile => "file",
            ResourceType.Folder => "folder",
            ResourceType.LiteMod => "litemod",
            _ => "unknown",
        },
        resource.SizeInfo,
        new DateTimeOffset(resource.DateTimeChanged).ToUnixTimeMilliseconds());

    private static AbsolutePath ScreenshotsDir(MinecraftInstance instance) =>
        (instance.GameRoot / "screenshots");

    private static readonly string[] ScreenshotExtensions = [".png", ".jpg", ".jpeg"];

    private static List<ScreenshotView> ListScreenshots(MinecraftInstance instance)
    {
        var dir = ScreenshotsDir(instance);
        if (!dir.ExistsDirectory())
        {
            return [];
        }

        var result = new List<ScreenshotView>();
        IEnumerable<AbsolutePath> files;
        try
        {
            files = dir.EnumerateFiles();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var file in files)
        {
            if (!ScreenshotExtensions.Contains(file.GetExtensionWithDot()))
            {
                continue;
            }

            var info = new FileInfo(file.Value);
            result.Add(new ScreenshotView(info.Name, info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }

        return result.OrderBy(view => view.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Resolves a listed screenshot name inside the screenshots folder, refusing
    /// anything with path separators or outside the folder (path traversal guard).</summary>
    private static AbsolutePath? ResolveScreenshotPath(AbsolutePath dir, string name)
    {
        var path = dir / name;
        return path.Exists() && path.StartsWith(dir)
            ? path
            : null;
    }
}
