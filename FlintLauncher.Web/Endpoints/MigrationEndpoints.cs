// SPDX-License-Identifier: GPL-3.0-only
// Migration endpoints: detect Prism-family data directories, plan the import, run it as a
// background task (move / CoW-clone / copy), and mark declined sources. Strictly
// user-triggered — there is no automatic startup migration.
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;
using Microsoft.AspNetCore.Http.Json;

namespace FlintLauncher.Web.Endpoints;

/// <summary>POST /migration/prism body.</summary>
public sealed record PrismImportRequest(string Source, string? Mode, bool DryRun);

/// <summary>POST /migration/prism/decline body。</summary>
public sealed record PrismDeclineRequest(string Source);

/// <summary>POST /migration/prism?dryRun=true response。</summary>
public sealed record PrismImportDryRunResponse(
    bool DryRun, long TotalFiles, long TotalBytes, PrismImportPlanItem[] Items);

/// <summary>GET /migration/prism/plan response (named record: AOT 源生成需要具体类型)。</summary>
public sealed record PrismImportPlanResponse(
    string Source, string Mode, long TotalFiles, long TotalBytes,
    PrismImportPlanItem[] Items);

public static class MigrationEndpoints
{
    public static void MapMigration(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/migration/prism");

        group.MapGet("/candidates", (LauncherRuntime runtime) =>
            Results.Ok(PrismSourceDetector.Detect(runtime.Paths.DataDir).ToArray()));

        group.MapGet("/plan", (LauncherRuntime runtime, string source, string? mode) =>
        {
            if (!TryParseMode(mode, out var importMode, out var error))
            {
                return ApiJson.Error("invalid_mode", error, 400);
            }
            if (!Directory.Exists(source))
            {
                return ApiJson.NotFound("source_not_found", $"source directory '{source}' does not exist");
            }
            var plan = PrismImportPlanner.Plan(source, runtime.Paths.DataDir, importMode);
            var totalFiles = plan.Sum(item => item.Files);
            var totalBytes = plan.Sum(item => item.Bytes);
            return Results.Ok(new PrismImportPlanResponse(source, importMode.ToString(), totalFiles, totalBytes,
                plan.ToArray()));
        });

        group.MapPost("/", async (LauncherRuntime runtime, FlintTaskRegistry registry, PrismImportRequest request) =>
        {
            if (!TryParseMode(request.Mode, out var importMode, out var error))
            {
                return ApiJson.Error("invalid_mode", error, 400);
            }
            if (!Directory.Exists(request.Source))
            {
                return ApiJson.NotFound("source_not_found", $"source directory '{request.Source}' does not exist");
            }

            if (request.DryRun)
            {
                var plan = PrismImportPlanner.Plan(request.Source, runtime.Paths.DataDir, importMode);
                return Results.Ok(new PrismImportDryRunResponse(
                    DryRun: true,
                    TotalFiles: plan.Sum(item => item.Files),
                    TotalBytes: plan.Sum(item => item.Bytes),
                    Items: plan.ToArray()));
            }

            var task = new PrismImportTask(request.Source, runtime.Paths.DataDir, importMode);
            task.Succeeded += _ => OnImportFinished(runtime);
            task.Failed += (_, _) => OnImportFinished(runtime);
            var entry = registry.Register("prism-import", task, onFinished: null);
            _ = task.StartAsync();
            return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
        });

        group.MapPost("/decline", (LauncherRuntime runtime, PrismDeclineRequest request) =>
        {
            var name = LegacyNameFor(request.Source);
            DataMigration.SetDoNotMigrate(runtime.Paths.DataDir, name);
            return Results.NoContent();
        });
    }

    /// <summary>Instances/meta appear under the data dir as files land — reload the live
    /// state once the import task reaches a terminal state.</summary>
    private static void OnImportFinished(LauncherRuntime runtime)
    {
        try
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
        }
        catch
        {
            // a reload failure must not break the task terminal event
        }
    }

    private static bool TryParseMode(string? mode, out ImportMode parsed, out string error)
    {
        switch ((mode ?? "cow").Trim().ToLowerInvariant())
        {
            case "" or "cow":
                parsed = ImportMode.CopyOnWrite;
                error = "";
                return true;
            case "move":
                parsed = ImportMode.Move;
                error = "";
                return true;
            case "copy":
                parsed = ImportMode.Copy;
                error = "";
                return true;
            default:
                parsed = ImportMode.CopyOnWrite;
                error = "mode must be one of 'cow', 'move', 'copy'";
                return false;
        }
    }

    private static string LegacyNameFor(string sourceDir)
    {
        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        foreach (var (name, _) in PrismSourceDetector.KnownLaunchers)
        {
            if (dirName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return "prism";
    }
}
