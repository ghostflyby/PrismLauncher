// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Security;
using FlintLauncher.Core;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.State;
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

/// <summary>GET /migration/flint/candidates response.</summary>
public sealed record FlintMigrationCandidatesResponse(
    string? LegacyRoot,
    bool RemountDecided,
    string? IniChainFile,
    int PendingInstances,
    bool Any);

/// <summary>GET /migration/prism/candidates response item — the wire projection of a
/// detected source. The domain record carries typed paths; the wire stays strings so the
/// typed path never enters the serialization graph at all.</summary>
public sealed record PrismSourceCandidateView(
    string Path,
    string LegacyName,
    string ConfigFile,
    bool ConfigFound,
    bool MarkerPresent,
    bool TargetHasOwnConfig,
    int Decision,
    int InstancesFound);

public static class MigrationEndpoints
{
    public static void MapMigration(this RouteGroupBuilder api)
    {
        api.WithTags("Migration");
        var group = api.MapGroup("/migration/prism");

        group.MapGet("/candidates", (PrismSourceCatalog catalog) =>
                Results.Ok(catalog.Detect().Select(ToView).ToArray()))
            .Produces<PrismSourceCandidateView[]>();

        group.MapGet("/plan", (PrismSourceCatalog catalog, FlintPaths paths, string source, string? mode) =>
        {
            if (!TryParseMode(mode, out var importMode, out var error))
            {
                return ApiJson.Error("invalid_mode", error, 400);
            }

            // the plan view enumerates a directory's full inventory — a raw path here is an
            // arbitrary-directory probe primitive, so the source must be one of the detected
            // candidates (see /candidates)
            if (string.IsNullOrWhiteSpace(source))
            {
                return ApiJson.Error("invalid_source", "the source directory is required", 400);
            }

            if (!catalog.IsKnownSource(source))
            {
                return ApiJson.Error("invalid_source",
                    $"source directory '{source}' is not a detected migration candidate", 400);
            }

            if (!Directory.Exists(source))
            {
                return ApiJson.NotFound("source_not_found", $"source directory '{source}' does not exist");
            }

            // the one HTTP-boundary parse: the request DTO stays an unvalidated string,
            // everything below is the typed domain. The parse accepts only fully qualified
            // paths — on Windows a rooted string without a drive letter is not absolute for
            // TruePath 2 (its full-path spelling can still match a detected candidate), and
            // a malformed source is a bad request, not a fault.
            if (!Path.IsPathFullyQualified(source))
            {
                return ApiJson.Error("invalid_source",
                    $"source directory '{source}' is not a fully qualified path", 400);
            }

            AbsolutePath sourceDir = new(source);
            var plan = PrismImportPlanner.Plan(sourceDir, paths.DataDir, importMode);
            var totalFiles = plan.Sum(item => item.Files);
            var totalBytes = plan.Sum(item => item.Bytes);
            return Results.Ok(new PrismImportPlanResponse(source, importMode.ToString(), totalFiles, totalBytes,
                plan.ToArray()));
        }).Produces<PrismImportPlanResponse>();

        group.MapPost("/", async (PrismSourceCatalog catalog, FlintPaths paths, MigrationDecisionStore migrationDecisions,
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
            // everything below is the typed domain. The parse accepts only fully qualified
            // paths — on Windows a rooted string without a drive letter is not absolute for
            // TruePath 2, and a malformed source is a bad request, not a fault.
            if (!Path.IsPathFullyQualified(request.Source))
            {
                return ApiJson.Error("invalid_source",
                    $"source directory '{request.Source}' is not a fully qualified path", 400);
            }

            AbsolutePath sourceDir = new(request.Source);

            if (request.DryRun)
            {
                // the dry-run preview is the same full-inventory enumeration as /plan — the
                // same probe primitive, the same candidate gate
                if (!catalog.IsKnownSource(request.Source))
                {
                    return ApiJson.Error("invalid_source",
                        $"source directory '{request.Source}' is not a detected migration candidate", 400);
                }

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

            // every terminal state completes the single pass: the C++ task emitted finished()
            // after emitAborted() too (Task.cpp:133-146), so a cancelled import ran the same
            // completion as a successful one — an aborted import otherwise strands what
            // already landed at the data root (assets/libraries/meta re-downloaded GB-level by
            // the store side until the next process start)
            var entry = registry.Start("prism-import", async handle =>
            {
                try
                {
                    await handle.RunAsync(ct => task.ImportAsync(handle, ct)).ConfigureAwait(false);
                }
                finally
                {
                    completion.Run();
                }
            }, instanceId: null);

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

        // ---- the flint side: one manual run over this install's own legacy material -------
        var flint = api.MapGroup("/migration/flint");

        flint.MapGet("/candidates", (FlintPaths paths, MigrationDecisionStore migrationDecisions,
            InstanceList instances) =>
        {
            var candidates = FlintMigration.Detect(paths, migrationDecisions, instances.InstanceDirs);
            return Results.Ok(new FlintMigrationCandidatesResponse(
                candidates.LegacyRoot?.Value, candidates.RemountDecided,
                candidates.IniChainFile?.Value, candidates.PendingInstances, candidates.Any));
        }).Produces<FlintMigrationCandidatesResponse>();

        flint.MapPost("/", (FlintPaths paths, MigrationDecisionStore migrationDecisions,
            FlintConfigStore config, ISecretStore secrets, LauncherStateStore state,
            InstanceList instances, InstanceMutationLock instanceLock, FlintTaskRegistry registry) =>
        {
            // the remount half can move gigabytes — it runs as a background task like the
            // prism import; each half is idempotent, so the task is safe to re-run
            var entry = registry.Start("flint-migration", async handle =>
            {
                try
                {
                    await handle.RunAsync(token => Task.Run(() => FlintMigration.RunAll(
                        paths, migrationDecisions, config, secrets, state,
                        instances.InstanceDirs,
                        instances.Environment.ConfigInfo, instances.Environment.StateInfo),
                        token)).ConfigureAwait(false);
                }
                finally
                {
                    // the conversion materialized instance directories: the live list must
                    // see them, serialized like every endpoint mutation of the list
                    instanceLock.Wait();
                    try
                    {
                        instances.LoadList();
                    }
                    finally
                    {
                        instanceLock.Release();
                    }
                }
            }, instanceId: null);

            return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
        }).Produces<TaskAcceptedResponse>(202);
    }

    /// <summary>The wire projection of one detected source: paths and the decision flatten
    /// to their string/number forms (the shape the endpoint served before the typed-path
    /// era, kept stable).</summary>
    private static PrismSourceCandidateView ToView(PrismSourceDetector.PrismSourceCandidate c) =>
        new(c.Path.Value, c.LegacyName, c.ConfigFile, c.ConfigFound, c.MarkerPresent,
            c.TargetHasOwnConfig, (int)c.Decision, c.InstancesFound);

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
