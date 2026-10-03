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
            Results.Ok(PrismSourceDetector.Detect(runtime.Paths.DataDir, runtime.MigrationDecisions,
                runtime.Paths.ConfigFile).ToArray()));

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
            // A3: an unknown directory never becomes a migration source — the old "prism"
            // fallback key would file ITS record under the canonical PrismLauncher source's
            // name and seal it away
            if (LegacyNameFor(request.Source) is not { } sourceName)
            {
                return ApiJson.Error("invalid_source",
                    $"source directory '{request.Source}' is not a known launcher data directory", 400);
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

            // the C++ existing-data protection (Application.cpp:2029/2055): a target that
            // already carries its own launcher config — post-reform the config root's
            // flint.json — never migrates ("Migration was skipped, due to existing data").
            // The dry-run preview above stays open: it is the "what would be imported" view.
            if (File.Exists(runtime.Paths.ConfigFile))
            {
                return ApiJson.Conflict("target_has_own_config",
                    "the target already has its own launcher config; migrate yourself or decline the source");
            }

            // S7: the task files its decision record under the ACTUAL legacy source name
            // (prism/polymc/multimc) — the old code hardcoded "prism"
            var task = new PrismImportTask(request.Source, runtime.Paths.DataDir, importMode,
                sourceName, runtime.MigrationDecisions);
            // every terminal state completes the single pass (GLOBAL G2): the C++ task emitted
            // finished() after emitAborted() too (Task.cpp:133-146), so a cancelled import ran
            // the same completion as a successful one. The port dropped finished() — the
            // subscribers must hang the hook off all three terminal states, or an aborted
            // import strands what already landed at the data root (assets/libraries/meta
            // re-downloaded GB-level by the store side until the next process start).
            task.Succeeded += _ => OnImportFinished(runtime);
            task.Failed += (_, _) => OnImportFinished(runtime);
            task.Aborted += _ => OnImportFinished(runtime);
            var entry = registry.Register("prism-import", task, onFinished: null);
            _ = task.StartAsync();
            return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
        });

        group.MapPost("/decline", (LauncherRuntime runtime, PrismDeclineRequest request) =>
        {
            // A3: same guard as the import — a decline is an auditable record and must never
            // seal the canonical source's key for an unrelated directory
            if (LegacyNameFor(request.Source) is not { } name)
            {
                return ApiJson.Error("invalid_source",
                    $"source directory '{request.Source}' is not a known launcher data directory", 400);
            }
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
    /// the launcher would re-download GBs until the next process start). The C5 INI sort and
    /// the account list rejoin the pass for the same reason: the whitelist lands
    /// prismlauncher.cfg/polymc.cfg/… and accounts.json at the data root, and the startup
    /// assembly steps that consume them (the config importer, AccountList.LoadList) already
    /// ran BEFORE any import existed — a session without this half would need a restart
    /// before the imported settings/secrets/state keys/accounts took effect, and a user
    /// writing any setting in between would create flint.json and seal the INI away
    /// forever (the one-shot guard).</summary>
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
            // Runs FIRST: it is what carries a flint.cfg onto the config root, where the
            // C5 importer's chain probe looks before the data root.
            FlintLegacyMigration.Execute(runtime.Paths);
        }
        catch
        {
            // a remount failure must not break the task terminal event
        }
        try
        {
            // the C5 single pass over what the import just landed. Runs against the LIVE
            // stores, so every destination is same-session visible without a restart:
            // the secret trio → ISecretStore (disk-backed, every read hits the file), the
            // C9 state keys → LauncherStateStore.Update (Current swaps in memory), the
            // rest → FlintConfigStore.Update (Current swaps AND flint.json lands — which
            // is itself the one-shot seal). The importer's own guards keep the semantics:
            // flint.json already present → Idle (the one-shot never re-fires, even when a
            // setting was written mid-import), no chain file at any root → Idle (this
            // import carried no INI — nothing to sort).
            Core.Config.FlintConfigImporter.ImportIfNeeded(runtime.Paths, runtime.Config,
                runtime.Secrets, runtime.State);
        }
        catch
        {
            // an import failure must not break the task terminal event
        }
        try
        {
            // accounts.json landed at its anchor (the data root) — reload so the same
            // session sees the imported accounts. A no-op when nothing landed: LoadList
            // returns false on a missing/unreadable file and keeps the in-memory list.
            runtime.Accounts.LoadList();
        }
        catch
        {
            // a reload failure must not break the task terminal event
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

    /// <summary>The legacy-launcher id a source directory files its S3 record under — the
    /// directory's own name matched (case-insensitively) against the KNOWN launcher
    /// directories (the same mapping the detector scans). Null when the directory is not a
    /// known launcher data directory: A3 — the old Contains match plus the "prism" fallback
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
