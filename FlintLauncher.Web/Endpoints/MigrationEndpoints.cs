// SPDX-License-Identifier: GPL-3.0-only
// Migration endpoints: detect Prism-family data directories, plan the import, run it as a
// background task (move / CoW-clone / copy), and record declined sources (STATE S3 decision
// records under the state root). The prism-family endpoints are strictly user-triggered —
// prism-family never auto-runs (G2); the flint-legacy profile is the one startup eager pass
// (LauncherRuntime assembly step 5) and is not served here.
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
            Results.Ok(PrismSourceDetector.Detect(runtime.Paths.DataDir, runtime.MigrationDecisions).ToArray()));

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

            // S7: the task files its decision record under the ACTUAL legacy source name
            // (prism/polymc/multimc) — the old code hardcoded "prism"
            var sourceName = LegacyNameFor(request.Source);
            var task = new PrismImportTask(request.Source, runtime.Paths.DataDir, importMode,
                sourceName, runtime.MigrationDecisions);
            task.Succeeded += _ => OnImportFinished(runtime);
            task.Failed += (_, _) => OnImportFinished(runtime);
            var entry = registry.Register("prism-import", task, onFinished: null);
            _ = task.StartAsync();
            return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
        });

        group.MapPost("/decline", (LauncherRuntime runtime, PrismDeclineRequest request) =>
        {
            var name = LegacyNameFor(request.Source);
            // S3: the decline is an auditable decision record, not an empty marker file
            runtime.MigrationDecisions.Write(new Core.Migration.MigrationDecisionRecord
            {
                Source = name,
                Decision = Core.Migration.MigrationDecisionRecord.Declined,
                At = DateTimeOffset.Now.ToString("o"),
            });
            return Results.NoContent();
        });
    }

    /// <summary>Instances/meta appear under the data dir as files land — reload the live
    /// state once the import task reaches a terminal state. The import pipeline's shared
    /// content (assets/libraries/meta) lands at the DATA root while the store/cache bases
    /// read from the five-root mounts, so the SAME session immediately runs the flint-legacy
    /// remount pass over what just landed (GLOBAL G2: single-pass completion — without it
    /// the launcher would re-download GBs until the next process start).</summary>
    private static void OnImportFinished(LauncherRuntime runtime)
    {
        try
        {
            // the RECORD-LESS remount entry: the S3 record must not gate this pass — it
            // seals the STARTUP auto-run only (an upgraded user carries the record
            // permanently, so the gated ExecuteIfNeeded would skip here forever and the
            // freshly imported assets/libraries/meta would stay stranded at the data root,
            // re-downloaded GB-level on the store side). The remount itself is idempotent;
            // a failed pass leaves the data at the data root for the next import's retry.
            FlintLegacyMigration.Execute(runtime.Paths);
        }
        catch
        {
            // a remount failure must not break the task terminal event
        }
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
