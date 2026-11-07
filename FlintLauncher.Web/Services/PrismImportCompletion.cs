// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.State;

namespace FlintLauncher.Web.Services;

/// <summary>The single-pass completion of a Prism import, run once the import task reaches
/// a terminal state. Instances/meta appear under the data dir as files land — reload the
/// live state. The import pipeline's shared content (assets/libraries/meta) lands at the
/// DATA root while the store/cache bases read from the five-root mounts, so the SAME
/// session immediately runs the flint-legacy remount pass over what just landed (without
/// it the launcher would re-download GBs until the next process start). The INI projection
/// and the eager instance.cfg conversion rejoin the pass for the same reason: the imported
/// prismlauncher.cfg and instance directories are legacy material the manual flint
/// migration would otherwise have to be triggered for, and the instances are invisible to
/// discovery until the conversion materializes their instance.json. The account list
/// reloads for the same session too.</summary>
public sealed class PrismImportCompletion(
    FlintPaths paths,
    FlintConfigStore config,
    ISecretStore secrets,
    LauncherStateStore state,
    MigrationDecisionStore migrationDecisions,
    AccountList accounts,
    InstanceList instances,
    InstanceMutationLock instanceLock)
{
    public void Run()
    {
        try
        {
            // the RECORD-LESS remount entry: the record must not gate this pass — it
            // seals the MANUAL migration only (an upgraded user carries the record
            // permanently, so the gated entry would skip here forever and the
            // freshly imported assets/libraries/meta would stay stranded at the data root,
            // re-downloaded GB-level on the store side). The remount itself is idempotent;
            // a failed pass leaves the data at the data root for the next import's retry.
            // Runs FIRST: it is what carries a flint.cfg onto the config root, where the
            // projection's chain probe looks before the data root.
            FlintLegacyMigration.Execute(paths);
        }
        catch
        {
            // a remount failure must not break the task terminal event
        }

        try
        {
            // the config projection over what the import landed: the secret trio →
            // ISecretStore, the state keys → LauncherStateStore, the rest → flint.json
            // (merge semantics — a value the user already wrote wins over the INI). A run
            // seals the candidate view's one-shot record: the chain file stays on disk and
            // would otherwise be re-proposed forever.
            if (FlintConfigImporter.Import(paths, config, secrets, state).ImportedIni is not null)
            {
                FlintMigration.SealConfigDecision(migrationDecisions);
            }
        }
        catch
        {
            // a projection failure must not break the task terminal event
        }

        try
        {
            // the imported instance directories carry only instance.cfg: materialize
            // instance.json (and the time trio's home) so discovery lists them
            LegacyInstanceConversion.Convert(instances.InstanceDirs,
                instances.Environment.ConfigInfo, instances.Environment.StateInfo);
        }
        catch
        {
            // a conversion failure must not break the task terminal event
        }

        try
        {
            // accounts.json landed at its anchor (the data root) — reload so the same
            // session sees the imported accounts. A no-op when nothing landed: LoadList
            // returns false on a missing/unreadable file and keeps the in-memory list.
            accounts.LoadList();
        }
        catch
        {
            // a reload failure must not break the task terminal event
        }

        try
        {
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
        catch
        {
            // a reload failure must not break the task terminal event
        }
    }
}
