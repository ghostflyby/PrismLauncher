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
/// it the launcher would re-download GBs until the next process start). The INI sort and
/// the account list rejoin the pass for the same reason: the whitelist lands
/// prismlauncher.cfg/polymc.cfg/… and accounts.json at the data root, and the startup
/// assembly that consumes them (the config importer, AccountList.LoadList) already ran
/// BEFORE any import existed — a session without this half would need a restart before
/// the imported settings/secrets/state keys/accounts took effect, and a user writing any
/// setting in between would create flint.json and seal the INI away forever (the one-shot
/// guard).</summary>
public sealed class PrismImportCompletion(
    FlintPaths paths,
    FlintConfigStore config,
    ISecretStore secrets,
    LauncherStateStore state,
    AccountList accounts,
    InstanceList instances,
    InstanceMutationLock instanceLock)
{
    public void Run()
    {
        try
        {
            // the RECORD-LESS remount entry: the record must not gate this pass — it
            // seals the STARTUP auto-run only (an upgraded user carries the record
            // permanently, so the gated ExecuteIfNeeded would skip here forever and the
            // freshly imported assets/libraries/meta would stay stranded at the data root,
            // re-downloaded GB-level on the store side). The remount itself is idempotent;
            // a failed pass leaves the data at the data root for the next import's retry.
            // Runs FIRST: it is what carries a flint.cfg onto the config root, where the
            // importer's chain probe looks before the data root.
            FlintLegacyMigration.Execute(paths);
        }
        catch
        {
            // a remount failure must not break the task terminal event
        }

        try
        {
            // the single pass over what the import just landed. Runs against the LIVE
            // stores, so every destination is same-session visible without a restart:
            // the secret trio → ISecretStore (disk-backed, every read hits the file), the
            // state keys → LauncherStateStore.Update (Current swaps in memory), the
            // rest → FlintConfigStore.Update (Current swaps AND flint.json lands — which
            // is itself the one-shot seal). The importer's own guards keep the semantics:
            // flint.json already present → Idle (the one-shot never re-fires, even when a
            // setting was written mid-import), no chain file at any root → Idle (this
            // import carried no INI — nothing to sort).
            FlintConfigImporter.ImportIfNeeded(paths, config, secrets, state);
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
