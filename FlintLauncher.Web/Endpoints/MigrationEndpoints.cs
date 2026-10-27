// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Migration endpoints: detect Prism-family data directories, plan the import, run it as a
// background task (move / CoW-clone / copy), and record declined sources (decision
// records under the state root). The prism-family endpoints are strictly user-triggered —
// prism-family never auto-runs; the flint-legacy profile is the one startup eager pass
// (composed by WebHostBuilder, which logs its outcome) and is not served here.

using System.Security;
using FlintLauncher.Core;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;
using TruePath;

namespace FlintLauncher.Web.Endpoints;

/// <summary>POST /migration/prism body.</summary>
public sealed record PrismImportRequest(string Source, string? Mode, bool DryRun);

/// <summary>POST /migration/prism/decline body。</summary>
public sealed record PrismDeclineRequest(string Source);

/// <summary>POST /migration/prism?dryRun=true response。</summary>
public sealed record PrismImportDryRunResponse(
    bool DryRun,
    long TotalFiles,
    long TotalBytes,
    PrismImportPlanItem[] Items);

/// <summary>GET /migration/prism/plan response (a named record: AOT source generation needs a concrete type).</summary>
public sealed record PrismImportPlanResponse(
    string Source,
    string Mode,
    long TotalFiles,
    long TotalBytes,
    PrismImportPlanItem[] Items);

public static class MigrationEndpoints
{
    public static void MapMigration(this RouteGroupBuilder api)
    {
        api.WithTags("Migration");
        var group = api.MapGroup("/migration/prism");

        group.MapGet("/candidates", (FlintPaths paths, MigrationDecisionStore migrationDecisions) =>
                Results.Ok(PrismSourceDetector.Detect(paths.DataDir, migrationDecisions,
                    paths.ConfigFile).ToArray()))
            .Produces<PrismSourceDetector.PrismSourceCandidate[]>();

        group.MapGet("/plan", (FlintPaths paths, string source, string? mode) =>
        {
            if (!TryParseMode(mode, out var importMode, out var error))
            {
                return ApiJson.Error("invalid_mode", error, 400);
            }

            if (!Directory.Exists(source))
            {
                return ApiJson.NotFound("source_not_found", $"source directory '{source}' does not exist");
            }

            // the one HTTP-boundary parse: the request DTO stays an unvalidated string,
            // everything below is the typed domain
            AbsolutePath sourceDir = new(source);
            var plan = PrismImportPlanner.Plan(sourceDir, paths.DataDir, importMode);
            var totalFiles = plan.Sum(item => item.Files);
            var totalBytes = plan.Sum(item => item.Bytes);
            return Results.Ok(new PrismImportPlanResponse(source, importMode.ToString(), totalFiles, totalBytes,
                plan.ToArray()));
        }).Produces<PrismImportPlanResponse>();

        group.MapPost("/", async (FlintPaths paths, MigrationDecisionStore migrationDecisions,
            PrismImportCompletion completion, FlintTaskRegistry registry, PrismImportRequest request) =>
        {
            if (!TryParseMode(request.Mode, out var importMode, out var error))
            {
                return ApiJson.Error("invalid_mode", error, 400);
            }

            if (!Directory.Exists(request.Source))
            {
                return ApiJson.NotFound("source_not_found", $"source directory '{request.Source}' does not exist");
            }

            // an unknown directory never becomes a migration source — the old "prism"
            // fallback key would file ITS record under the canonical PrismLauncher source's
            // name and seal it away
            if (LegacyNameFor(request.Source) is not { } sourceName)
            {
                return ApiJson.Error("invalid_source",
                    $"source directory '{request.Source}' is not a known launcher data directory", 400);
            }

            // the one HTTP-boundary parse: the request DTO stays an unvalidated string,
            // everything below is the typed domain
            AbsolutePath sourceDir = new(request.Source);

            if (request.DryRun)
            {
                var plan = PrismImportPlanner.Plan(sourceDir, paths.DataDir, importMode);
                return Results.Ok(new PrismImportDryRunResponse(
                    DryRun: true,
                    TotalFiles: plan.Sum(item => item.Files),
                    TotalBytes: plan.Sum(item => item.Bytes),
                    Items: plan.ToArray()));
            }

            // the C++ existing-data protection (Application.cpp:2029/2055): a target that
            // already carries its own launcher config — post-reform the config root's
            // flint.json — never migrates ("Migration was skipped, due to existing data").
            // The dry-run preview above stays open: it is the "what would be imported" view.
            if (File.Exists(paths.ConfigFile.Value))
            {
                return ApiJson.Conflict("target_has_own_config",
                    "the target already has its own launcher config; migrate yourself or decline the source");
            }

            // the task files its decision record under the ACTUAL legacy source name
            // (prism/polymc/multimc) — the old code hardcoded "prism"
            var task = new PrismImportTask(sourceDir, paths.DataDir, importMode,
                sourceName, migrationDecisions);
            // every terminal state completes the single pass: the C++ task emitted
            // finished() after emitAborted() too (Task.cpp:133-146), so a cancelled import ran
            // the same completion as a successful one. The port dropped finished() — the
            // subscribers must hang the hook off all three terminal states, or an aborted
            // import strands what already landed at the data root (assets/libraries/meta
            // re-downloaded GB-level by the store side until the next process start).
            task.Succeeded += _ => completion.Run();
            task.Failed += (_, _) => completion.Run();
            task.Aborted += _ => completion.Run();
            var entry = registry.Register("prism-import", task, onFinished: null);
            _ = task.StartAsync();
            return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
        }).Produces<PrismImportDryRunResponse>().Produces<TaskAcceptedResponse>(202);

        group.MapPost("/decline", (MigrationDecisionStore migrationDecisions, PrismDeclineRequest request) =>
        {
            // same guard as the import — a decline is an auditable record and must never
            // seal the canonical source's key for an unrelated directory
            if (LegacyNameFor(request.Source) is not { } name)
            {
                return ApiJson.Error("invalid_source",
                    $"source directory '{request.Source}' is not a known launcher data directory", 400);
            }

            // the decline is an auditable decision record, not an empty marker file —
            // but the write is best-effort like the C++ setDoNotMigrate
            // (Application.cpp:2015-2026): the worst case is the source being re-proposed,
            // which is the honest undecided semantic, so an IO failure here must not surface
            // as a 500.
            try
            {
                migrationDecisions.Write(new MigrationDecisionRecord
                {
                    Source = name,
                    Decision = MigrationDecisionRecord.Declined,
                    At = DateTimeOffset.Now.ToString("o"),
                });
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                          or NotSupportedException or SecurityException)
            {
            }

            return Results.NoContent();
        }).Produces(204);
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

    /// <summary>The legacy-launcher id a source directory files its record under — the
    /// directory's own name matched (case-insensitively) against the KNOWN launcher
    /// directories (the same mapping the detector scans). Null when the directory is not a
    /// known launcher data directory — the old Contains match plus the "prism" fallback
    /// let any directory (e.g. <c>~/prism-backup</c>) write its decision record under the
    /// canonical PrismLauncher source's key, permanently sealing it away.</summary>
    private static string? LegacyNameFor(string sourceDir)
    {
        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        foreach (var (name, _) in PrismSourceDetector.KnownLaunchers)
        {
            if (string.Equals(dirName, PrismSourceDetector.NameToDirectory(name),
                    StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }
}
