// SPDX-License-Identifier: GPL-3.0-only
// The global-side INI import pipeline (CONFIG C5 §2.4 + the MultiMC dual-name rider):
// when flint.json is missing, the first file of the INI chain
//     flint.cfg ← prismlauncher.cfg / polymc.cfg / multimc.cfg | mmc.cfg
// (probed under the config root, then the pre-reform flat data root) is imported ONCE.
// "Once" = the import writes flint.json, which is the guard — the INI stays on disk
// untouched (C5: the write side is JSON-only now; read-compat only).
//
// The single pass sorts every INI key (CONFIG C4 + C9 + the key registry):
//   1. secret trio (ProxyPass/ModrinthToken/FlameKeyOverride)  → ISecretStore
//   2. C9 pseudo-config keys (playtime, java probe, offline name, the wizard ask)
//      → launcher-state.json (the java probe QUINTET is dropped: S5's cache is keyed by the
//      probed binary path, which the INI never recorded — it is a pure optimization that
//      re-probes on the next launch)
//   3. everything else → the record domains through the shared key registry (unknown keys
//      — GUI geometry blobs, themes, ConfigVersion — stay in the INI, unread)
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;

namespace FlintLauncher.Core.Config;

public static class FlintConfigImporter
{
    /// <summary>What the one-shot import did (the host logs it after Build — the assembly
    /// sequence runs the importer before any logger exists, so the outcome travels on the
    /// runtime like the flint-legacy result). <see cref="ImportedIni"/> is the chain file
    /// that was imported (null = nothing was imported); <see cref="UnreadableIni"/> is the
    /// chain file that exists but could not be parsed — the silent failure this record
    /// exists to surface (a default flint.json starts behind an unreadable INI, which the
    /// user should hear about, not guess).</summary>
    public sealed record ImportResult(string? ImportedIni, string? UnreadableIni)
    {
        /// <summary>The guarded / no-chain-file no-ops (both are normal, not failures).</summary>
        public static readonly ImportResult Idle = new(null, null);
    }

    /// <summary>The INI chain, in preference order (CONFIG §2.4 + the rider: MultiMC's
    /// multimc.cfg and the historical detector name mmc.cfg are both probed).</summary>
    public static readonly IReadOnlyList<string> IniChain =
    [
        "flint.cfg",
        "prismlauncher.cfg",
        "polymc.cfg",
        "multimc.cfg",
        "mmc.cfg",
    ];

    /// <summary>C9 runtime-state keys: their INI names and the state fields they land in.</summary>
    private const string KeyTotalPlayTime = "TotalPlayTime";
    private const string KeyTotalPlayTimeMigrated = "TotalPlayTimeMigrated";
    private const string KeyUserAskedAboutAutomaticJavaDownload = "UserAskedAboutAutomaticJavaDownload";
    private const string KeyLastOfflinePlayerName = "LastOfflinePlayerName";
    private const string KeyLastHostname = "LastHostname";

    /// <summary>The java probe quintet: recognized as state, but NOT importable (S5 keys the
    /// cache by the probed binary path, which the INI never carried).</summary>
    public static readonly IReadOnlyList<string> JavaProbeKeys =
    [
        "JavaSignature",
        "JavaArchitecture",
        "JavaRealArchitecture",
        "JavaVersion",
        "JavaVendor",
    ];

    /// <summary>Run the one-shot import. Returns what happened: the imported INI path, the
    /// unreadable INI path, or <see cref="ImportResult.Idle"/> when flint.json already
    /// exists (the one-shot guard) or no chain file was found (fresh install — flint.json
    /// is not written until the first real write).</summary>
    public static ImportResult ImportIfNeeded(FlintPaths paths, FlintConfigStore config,
        ISecretStore secrets, LauncherStateStore state)
    {
        if (File.Exists(config.FilePath))
        {
            return ImportResult.Idle; // flint.json is authoritative — the one-shot already happened
        }

        if (FindChainFile(paths) is not { } iniPath)
        {
            return ImportResult.Idle;
        }

        var ini = new IniFile();
        if (!ini.LoadFile(iniPath))
        {
            // the INI exists but cannot be parsed: flint.json stays default — report it
            // instead of dying silently (the INI is kept on disk; C5 reads JSON only)
            return new ImportResult(null, iniPath);
        }

        // ---- single pass over the INI: sort every key to its destination -------------------
        var resolved = config.Current;
        long totalPlayTime = 0;
        var playtimeMigrated = false;
        string lastOfflinePlayerName = "";
        string lastHostname = "";
        var userAskedAboutAutomaticJavaDownload = false;
        var sawStateKeys = false;

        foreach (var key in ini.Keys)
        {
            if (key == QSettingsIniCodec.ConfigVersionKey)
            {
                continue;
            }
            var value = ini.Get(key);
            if (value is null || value.ValueKind == IniValue.Kind.Invalid)
            {
                continue;
            }

            // (1) the secret trio → the store, verbatim strings (C4)
            if (FlintConfigKeys.IsSecret(key))
            {
                var secret = Variant.AsString(Variant.FromIniValue(value));
                if (secret.Length != 0)
                {
                    secrets.Set(key, secret);
                }
                continue;
            }

            // (2) the C9 pseudo-config keys → launcher-state.json
            if (key == KeyTotalPlayTime)
            {
                totalPlayTime = Variant.AsLong(Variant.FromIniValue(value));
                sawStateKeys = true;
                continue;
            }
            if (key == KeyTotalPlayTimeMigrated)
            {
                playtimeMigrated = Variant.AsBool(Variant.FromIniValue(value));
                sawStateKeys = true;
                continue;
            }
            if (key == KeyUserAskedAboutAutomaticJavaDownload)
            {
                userAskedAboutAutomaticJavaDownload = Variant.AsBool(Variant.FromIniValue(value));
                sawStateKeys = true;
                continue;
            }
            if (key == KeyLastOfflinePlayerName)
            {
                lastOfflinePlayerName = Variant.AsString(Variant.FromIniValue(value));
                sawStateKeys = true;
                continue;
            }
            if (key == KeyLastHostname || JavaProbeKeys.Contains(key))
            {
                // LastHostname seeds the state's rescan anchor; the probe quintet is dropped
                // (S5's map needs the probed path — the INI never recorded it)
                if (key == KeyLastHostname)
                {
                    lastHostname = Variant.AsString(Variant.FromIniValue(value));
                }
                sawStateKeys = true;
                continue;
            }

            // (3) the rest → the record domains (unknown keys stay unread in the INI)
            if (FlintConfigKeys.Resolve(key) is { Secret: false } entry)
            {
                resolved = entry.Apply(resolved, Variant.FromIniValue(value));
            }
        }

        // ---- land the results: one state write, one config write (the one-shot seal) -------
        if (sawStateKeys)
        {
            state.Update(current => current with
            {
                Playtime = new PlaytimeState
                {
                    Total = totalPlayTime != 0 ? totalPlayTime : current.Playtime.Total,
                    Migrated = playtimeMigrated || current.Playtime.Migrated,
                },
                UserAskedAboutAutomaticJavaDownload = userAskedAboutAutomaticJavaDownload ||
                    current.UserAskedAboutAutomaticJavaDownload,
                LastOfflinePlayerName = lastOfflinePlayerName.Length != 0
                    ? lastOfflinePlayerName
                    : current.LastOfflinePlayerName,
                Java = lastHostname.Length != 0
                    ? current.Java with { LastHostname = lastHostname }
                    : current.Java,
            });
        }

        // write flint.json even when the INI carried no recognized key: the file's existence
        // IS the one-shot guard, so a re-ordered probe can't resurrect stale INI values later
        config.Update(_ => resolved);
        return new ImportResult(iniPath, null);
    }

    /// <summary>The first existing chain file: the config root (the new authoritative home)
    /// wins over the pre-reform flat data root, chain order as declared.</summary>
    public static string? FindChainFile(FlintPaths paths)
    {
        foreach (var dir in new[] { paths.ConfigRoot, paths.DataRoot })
        {
            foreach (var name in IniChain)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
