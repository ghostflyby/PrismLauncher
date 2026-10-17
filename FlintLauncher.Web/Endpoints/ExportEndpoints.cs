// SPDX-License-Identifier: GPL-3.0-only
// Export + log-file endpoints (UI-API mapping Wave B): POST /instances/{id}/export drives
// the ExportInstanceDialog/ExportPackDialog/ExportToModListDialog flows as registered tasks
// (202 + taskId — mrpack via ModrinthPackExportTask, CurseForge via FlamePackExportTask, the
// mod list via the local ModListExportTask), and GET /instances/{id}/logs[/{dir}/{name}]
// serves the OtherLogsPage surface (game logs + crash reports, content capped to a 512KB
// tail).

using System.Text;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Helpers;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.OpenApi;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class ExportEndpoints
{
    /// <summary>GET /instances/{id}/logs/{name} responses are capped to this many bytes
    /// (tail — the last bytes of the file, starting at the next line break).</summary>
    public const int MaxLogBytes = 512 * 1024;

    public static void MapExport(this RouteGroupBuilder api)
    {
        api.WithTags("Export");
        // -------------------------------------------------------------------- export
        api.MapPost("/instances/{id}/export", async (string id, ExportRequest request,
            LauncherRuntime runtime, FlintTaskRegistry registry) =>
        {
            var format = (request.Format ?? "").Trim().ToLowerInvariant();
            if (format is not ("modrinth" or "flame" or "modlist"))
            {
                return ApiJson.Error("invalid_format",
                    "'format' must be one of modrinth|flame|modlist", 400);
            }

            if (format == "modlist")
            {
                var format2 = request.Format2 is null ? "html" : request.Format2.Trim().ToLowerInvariant();
                if (!ModListExportTask.TryParseFormat(format2, out _, out _))
                {
                    return ApiJson.Error("invalid_format",
                        "'format2' must be one of html|markdown|plain|json|csv", 400);
                }
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                // the export inputs (the dialog fields): name/version fall back to the
                // instance's identity; the file lands in the instance directory
                var name = string.IsNullOrWhiteSpace(request.Name) ? instance.Name() : request.Name.Trim();
                var versionId = string.IsNullOrWhiteSpace(request.VersionId) ? "1.0" : request.VersionId.Trim();
                var instanceRoot = instance.InstanceRoot();
                Directory.CreateDirectory(instanceRoot);
                // DirNameFromString strips characters that are invalid in paths (like the
                // C++ export file-name suggestion)
                var baseName = FileSystem.DirNameFromString($"{name}-{versionId}", instanceRoot);
                var extension = format switch
                {
                    "modrinth" => "mrpack",
                    "flame" => "zip",
                    // the modlist leg appends "-mods" after the base name (the C++ dialog's
                    // default file suggestion)
                    _ => "",
                };
                var output = format == "modlist"
                    ? ""
                    : Path.Combine(instanceRoot, baseName + "." + extension);

                FlintTask task;
                var kind = format switch
                {
                    "modrinth" => "export-modrinth",
                    "flame" => "export-flame",
                    _ => "export-modlist",
                };
                switch (format)
                {
                    case "modrinth":
                        task = new ModrinthPackExportTask(name, versionId, request.Summary ?? "",
                            optionalFiles: true, instance, output, modrinth: runtime.Modrinth);
                        break;
                    case "flame":
                        task = new FlamePackExportTask(new FlamePackExportTask.Options
                        {
                            Name = name,
                            Version = versionId,
                            Author = request.Author ?? "",
                            OptionalFiles = true,
                            Instance = instance,
                            Output = output,
                            Flame = runtime.Flame,
                        });
                        break;
                    default:
                    {
                        ModListExportTask.TryParseFormat(request.Format2, out var modlistFormat,
                            out var modlistExtension);
                        output = Path.Combine(instanceRoot, baseName + "-mods." + modlistExtension);
                        task = new ModListExportTask(instance, output, modlistFormat,
                            OptionalData.Version | OptionalData.FileName);
                        break;
                    }
                }

                var entry = registry.Register(kind, task, instanceId: instance.Id());
                // publish the written file path through the task view's optional result once
                // the task actually succeeded (a failed export has no artifact)
                task.Succeeded += _ => entry.Result = new TaskResultView(null, null, output);
                _ = task.StartAsync(); // 202: the export runs in the background
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        }).Produces<TaskAcceptedResponse>(202);

        // ----------------------------------------------------------------- log listing
        api.MapGet("/instances/{id}/logs", async (string id, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                return Results.Ok(ListLogFiles(instance).ToArray());
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        }).Produces<LogFileView[]>();

        // ---------------------------------------------------------------- log contents
        api.MapGet("/instances/{id}/logs/{dir}/{name}", async (string id, string dir, string name,
            LauncherRuntime runtime) =>
        {
            if (dir is not ("logs" or "crash-reports"))
            {
                return ApiJson.Error("unknown_dir", "dir must be one of logs|crash-reports", 400);
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var path = ResolveLogFile(LogDir(instance, dir), name);
                if (path is null)
                {
                    return ApiJson.NotFound("log_not_found", $"no log file '{name}' in '{dir}'");
                }

                return Results.Text(ReadTail(path, MaxLogBytes), "text/plain",
                    Encoding.UTF8);
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        }).WithMetadata(new PlainTextMarker());
    }

    // ----------------------------------------------------------------------- helpers

    /// <summary>The MinecraftInstance for a {ref} (logs/exports need the game root).
    /// D2: every endpoint resolves through the addressing chain (GLOBAL §1).</summary>
    private static MinecraftInstance? LookupInstance(LauncherRuntime runtime, string reference) =>
        runtime.Instances.ResolveInstance(reference) as MinecraftInstance;

    private static string LogDir(MinecraftInstance instance, string dir) =>
        Path.Combine(instance.GameRoot(), dir);

    private static List<LogFileView> ListLogFiles(MinecraftInstance instance)
    {
        var result = new List<LogFileView>();
        foreach (var dir in new[] { "logs", "crash-reports" })
        {
            var path = LogDir(instance, dir);
            if (!Directory.Exists(path))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    continue;
                }

                result.Add(new LogFileView(
                    info.Name,
                    dir,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
            }
        }

        return result
            .OrderByDescending(view => view.ModifiedAt)
            .ThenBy(view => view.Dir, StringComparer.Ordinal)
            .ThenBy(view => view.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Resolves a listed log name inside the given folder, refusing anything with
    /// path separators or outside the folder (path traversal guard).</summary>
    private static string? ResolveLogFile(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(dir, name);
        var fullDir = Path.GetFullPath(dir);
        var full = Path.GetFullPath(path);
        return File.Exists(full) && full.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : null;
    }

    /// <summary>The last <paramref name="maxBytes"/> of a text file, starting on a line
    /// boundary; the whole file when it is smaller than the cap.</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        var length = new FileInfo(path).Length;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (length <= maxBytes)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }

            stream.Seek(length - maxBytes, SeekOrigin.Begin);
            var buffer = new byte[maxBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            // skip the (probably partial) first line
            var newline = text.IndexOf('\n');
            return newline >= 0 && newline < text.Length - 1 ? text[(newline + 1)..] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
