// SPDX-License-Identifier: GPL-3.0-only
// The global-side INI import pipeline (CONFIG C5 §2.4 + the MultiMC dual-name rider):
// when flint.json is missing, the first file of the INI chain
//     flint.cfg ← prismlauncher.cfg / polymc.cfg / multimc.cfg | mmc.cfg
// (probed under the config root, then the pre-reform flat data root) is imported ONCE.
// "Once" = the import writes flint.json, which is the guard — the INI stays on disk
// untouched (C5: the write side is JSON-only now; read-compat only).
//
// The single pass sorts every INI key (CONFIG C4 + C9 + the key registry):
//   0. the C++ Setting synonyms fold onto the primary key first (E1: ProxyPassword→ProxyPass,
//      CFKeyOverride→FlameKeyOverride, MCWindowWidth→MinecraftWinWidth, … — the old INI
//      spellings must land, not drop); when both spellings are present the primary wins,
//      like INISettingsObject::retrieveValue walking configKeys() primary-first
//   1. secret trio (ProxyPass/ModrinthToken/FlameKeyOverride — aliases included) → ISecretStore
//      under the PRIMARY name (C4)
//   2. C9 pseudo-config keys (java probe, offline name, the wizard ask) → launcher-state.json
//      (the java probe QUINTET is dropped: S5's cache is keyed by the probed binary path,
//      which the INI never recorded — it is a pure optimization that re-probes on the next
//      launch). TotalPlayTime/TotalPlayTimeMigrated keep an in-chain branch only as a
//      compatibility fallback: their C++ home is <data-root>/playtime.cfg, probed separately
//      beside the chain (E2 — the chain never carried them in a real Prism install)
//   3. everything else → the record domains through the shared key registry (unknown keys
//      — GUI geometry blobs, themes, ConfigVersion — stay in the INI, unread)
//
// After the pass, the automatic-java pair is re-derived the way the C++ did (E3): with a
// non-empty imported JavaPath and no explicit AutomaticJavaSwitch/Download key, the pair
// lands false in flint.json (Application.cpp computes the default from the INI-loaded
// JavaPath before registering the keys; the typed records default them true unconditionally).

using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;
using TruePath;
using TruePath.SystemIo;

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
        if (config.FilePath.ExistsFile())
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
            return new ImportResult(null, iniPath.Value);
        }

        // ---- single pass over the INI: sort every key to its destination -------------------
        var resolved = config.Current;
        long? chainTotalPlayTime = null;
        bool? chainPlaytimeMigrated = null;
        string lastOfflinePlayerName = "";
        string lastHostname = "";
        var userAskedAboutAutomaticJavaDownload = false;
        var sawStateKeys = false;
        var sawExplicitAutomaticJavaSwitch = false;
        var sawExplicitAutomaticJavaDownload = false;

        // E2: the playtime pair's real home — TotalPlayTime/TotalPlayTimeMigrated never lived
        // in the flint.cfg chain: the C++ registers them on a separate INISettingsObject opened
        // on the RELATIVE name "playtime.cfg" after chdir-ing to the data root
        // (Application.cpp:445 + :943-949; BaseInstance.cpp:312-315 accumulates there), so a
        // real prismlauncher.cfg never carries them. Probe <data-root>/playtime.cfg with the
        // same QSettings codec, inside the one-shot window (the flint.json seal below ends the
        // probing forever). The in-chain branches below stay as a compatibility fallback for
        // files that somehow do carry the keys; per member, the playtime file outranks them.
        var playtime = ReadPlaytimeFile(paths);

        foreach (var iniKey in ini.Keys)
        {
            if (iniKey == QSettingsIniCodec.ConfigVersionKey)
            {
                continue;
            }

            var value = ini.Get(iniKey);
            if (value is null || value.ValueKind == IniValue.Kind.Invalid)
            {
                continue;
            }

            // (0) E1: fold the C++ synonym spellings onto the primary key. When the file
            // carries BOTH spellings the primary wins regardless of file order — the C++
            // retrieveValue walks configKeys() primary-first.
            var key = FlintConfigKeys.Canonical(iniKey);
            if (key != iniKey && ini.Contains(key))
            {
                continue;
            }

            // (1) the secret trio → the store, verbatim strings, under the PRIMARY name (C4)
            if (FlintConfigKeys.IsSecret(key))
            {
                var secret = Variant.AsString(Variant.FromIniValue(value));
                if (secret.Length != 0)
                {
                    secrets.Set(key, secret);
                }

                continue;
            }

            // (2) the C9 pseudo-config keys → launcher-state.json (the playtime pair is the
            // in-chain compatibility fallback — see the E2 probe above)
            if (key == KeyTotalPlayTime)
            {
                chainTotalPlayTime = Variant.AsLong(Variant.FromIniValue(value));
                sawStateKeys = true;
                continue;
            }

            if (key == KeyTotalPlayTimeMigrated)
            {
                chainPlaytimeMigrated = Variant.AsBool(Variant.FromIniValue(value));
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

            // (2b) E3 bookkeeping: which half of the automatic-java pair the INI states
            // explicitly (each key's derived default only applies to the other half)
            if (key == "AutomaticJavaSwitch")
            {
                sawExplicitAutomaticJavaSwitch = true;
            }

            if (key == "AutomaticJavaDownload")
            {
                sawExplicitAutomaticJavaDownload = true;
            }

            // (3) the rest → the record domains (unknown keys stay unread in the INI)
            if (FlintConfigKeys.Resolve(key) is { Secret: false } entry)
            {
                resolved = entry.Apply(resolved, Variant.FromIniValue(value));
            }
        }

        // ---- E3: re-derive the automatic-java defaults the way the C++ did ------------------
        // Application.cpp:768-770 evaluates `JavaPath is empty` AFTER the INI loads and
        // registers the pair with that per-key default — a cfg that pinned JavaPath while
        // staying silent on the pair must NOT leave the typed records' unconditional true
        // standing, or the auto-java machinery overrides the user's JVM on the next launch
        // (AutoInstallJava.cs:52 / BaseInstance.cs:474). An explicit INI key wins for itself.
        if (!string.IsNullOrEmpty(resolved.Java.JavaPath))
        {
            if (!sawExplicitAutomaticJavaSwitch && resolved.Java.AutomaticJavaSwitch == true)
            {
                resolved = resolved with { Java = resolved.Java with { AutomaticJavaSwitch = false } };
            }

            if (!sawExplicitAutomaticJavaDownload && resolved.Java.AutomaticJavaDownload == true)
            {
                resolved = resolved with { Java = resolved.Java with { AutomaticJavaDownload = false } };
            }
        }

        // ---- land the results: one state write, one config write (the one-shot seal) -------
        // E2 merge: per member, the playtime.cfg probe outranks the in-chain fallback
        var playtimeTotal = playtime?.Total ?? chainTotalPlayTime ?? 0;
        var playtimeMigrated = playtime?.Migrated ?? chainPlaytimeMigrated ?? false;
        if (sawStateKeys || playtime is not null)
        {
            state.Update(current => current with
            {
                Playtime = new PlaytimeState
                {
                    Total = playtimeTotal != 0 ? playtimeTotal : current.Playtime.Total,
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
        return new ImportResult(iniPath.Value, null);
    }

    /// <summary>The first existing chain file: the config root (the new authoritative home)
    /// wins over the pre-reform flat data root, chain order as declared.</summary>
    public static AbsolutePath? FindChainFile(FlintPaths paths)
    {
        foreach (var dir in new[] { paths.ConfigRoot, paths.DataRoot })
        {
            foreach (var name in IniChain)
            {
                var candidate = new AbsolutePath(dir) / name;
                if (candidate.ExistsFile())
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The E2 playtime pair: which of TotalPlayTime/TotalPlayTimeMigrated carried a
    /// usable value (either member may be absent — a half-populated playtime.cfg merges
    /// member-by-member with the in-chain fallback).</summary>
    private sealed record PlaytimePair(long? Total, bool? Migrated);

    /// <summary>The C++ playtime home: <c>&lt;data-root&gt;/playtime.cfg</c> (the relative
    /// name the C++ INISettingsObject opened against the chdir'd data root), read with the
    /// same QSettings codec as the chain. Unreadable or key-less file → null (the probe is
    /// silent, exactly like the C++ defaults of 0/false behind a missing file).</summary>
    private static PlaytimePair? ReadPlaytimeFile(FlintPaths paths)
    {
        AbsolutePath file = paths.DataRoot / "playtime.cfg";
        if (!file.ExistsFile())
        {
            return null;
        }

        var ini = new IniFile();
        if (!ini.LoadFile(file))
        {
            return null;
        }

        long? total = null;
        if (ini.Get("TotalPlayTime") is { ValueKind: not IniValue.Kind.Invalid } totalValue)
        {
            total = Variant.AsLong(Variant.FromIniValue(totalValue));
        }

        bool? migrated = null;
        if (ini.Get("TotalPlayTimeMigrated") is { ValueKind: not IniValue.Kind.Invalid } migratedValue)
        {
            migrated = Variant.AsBool(Variant.FromIniValue(migratedValue));
        }

        return total is null && migrated is null ? null : new PlaytimePair(total, migrated);
    }
}
