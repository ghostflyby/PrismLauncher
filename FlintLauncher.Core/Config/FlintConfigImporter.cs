// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Config;

public static class FlintConfigImporter
{
    /// <summary>What the migration did (a host surfaces it). <see cref="ImportedIni"/> is the
    /// chain file that was projected (null = no chain file was found); <see cref="UnreadableIni"/>
    /// is the chain file that exists but could not be parsed — the silent failure this record
    /// exists to surface (a default flint.json starts behind an unreadable INI, which the
    /// user should hear about, not guess).</summary>
    public sealed record ImportResult(string? ImportedIni, string? UnreadableIni)
    {
        /// <summary>The no-chain-file no-op (normal, not a failure).</summary>
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

    /// <summary>Runtime-state keys: their INI names (recognized, landing in launcher-state).</summary>
    private const string KeyTotalPlayTime = "TotalPlayTime";

    private const string KeyTotalPlayTimeMigrated = "TotalPlayTimeMigrated";
    private const string KeyUserAskedAboutAutomaticJavaDownload = "UserAskedAboutAutomaticJavaDownload";
    private const string KeyLastOfflinePlayerName = "LastOfflinePlayerName";
    private const string KeyLastHostname = "LastHostname";

    /// <summary>The java probe quintet: recognized as state, but NOT importable (the cache keys the
    /// cache by the probed binary path, which the INI never carried).</summary>
    public static readonly IReadOnlyList<string> JavaProbeKeys =
    [
        "JavaSignature",
        "JavaArchitecture",
        "JavaRealArchitecture",
        "JavaVersion",
        "JavaVendor",
    ];

    /// <summary>Run the projection. Returns what happened: the projected INI path, the
    /// unreadable INI path, or <see cref="ImportResult.Idle"/> when no chain file was found
    /// (a fresh install — flint.json is not written until a real write or this migration).</summary>
    public static ImportResult Import(FlintPaths paths, FlintConfigStore config,
        ISecretStore secrets, LauncherStateStore state)
    {
        if (FindChainFile(paths) is not { } iniPath)
        {
            return ImportResult.Idle;
        }

        var ini = new IniFile();
        if (!ini.LoadFile(iniPath))
        {
            // the INI exists but cannot be parsed: flint.json stays default — report it
            // instead of dying silently (the INI is kept on disk; reads are JSON only)
            return new ImportResult(null, iniPath.Value);
        }

        // an explicit INI statement of the automatic-java pair beats the derived default
        var sawExplicitSwitch = LegacyConfigProjection.Find(ini, "AutomaticJavaSwitch", []) is not null;
        var sawExplicitDownload = LegacyConfigProjection.Find(ini, "AutomaticJavaDownload", []) is not null;
        var playtime = ReadPlaytimeFile(paths);

        // the raw payload as it sits on disk (absent members null): the projection merges onto
        // it — a missing file seeds the record defaults (the at-default rule must see defaults,
        // not a blank record, or a fresh install would fill nothing)
        FlintConfigFile Projected(FlintConfigFile raw)
        {
            raw = LegacyConfigProjection.Project(raw, ini);

            // re-derive the automatic-java defaults the way the C++ did (Application.cpp:768-770
            // evaluates `JavaPath is empty` AFTER the INI loads and registers the pair with that
            // per-key default): a cfg that pinned JavaPath while staying silent on the pair must
            // NOT leave the typed records' unconditional true standing, or the auto-java
            // machinery overrides the user's JVM on the next launch (AutoInstallJava.cs:52 /
            // BaseInstance.cs:474). An explicit value (an INI key or a customized flint.json
            // member — both read as non-default here) wins for itself.
            if (!string.IsNullOrEmpty(raw.Java.JavaPath))
            {
                if (!sawExplicitSwitch && raw.Java.AutomaticJavaSwitch == true)
                {
                    raw = raw with { Java = raw.Java with { AutomaticJavaSwitch = false } };
                }

                if (!sawExplicitDownload && raw.Java.AutomaticJavaDownload == true)
                {
                    raw = raw with { Java = raw.Java with { AutomaticJavaDownload = false } };
                }
            }

            return raw;
        }

        // ---- the secret trio → the store, verbatim strings, under the PRIMARY name --------
        // a stored secret is newer intent — the INI never overwrites one
        foreach (var (key, aliases) in LegacyConfigProjection.SecretKeys)
        {
            if (LegacyConfigProjection.Find(ini, key, aliases) is { } value &&
                string.IsNullOrEmpty(secrets.Get(key)) &&
                Variant.AsString(Variant.FromIniValue(value)) is { Length: > 0 } secret)
            {
                secrets.Set(key, secret);
            }
        }

        // ---- the pseudo-config keys → launcher-state.json (current wins per member) -------
        var chainTotalPlayTime = FindLong(ini, KeyTotalPlayTime);
        var chainPlaytimeMigrated = FindBool(ini, KeyTotalPlayTimeMigrated);
        var userAskedAboutAutomaticJavaDownload = FindBool(ini, KeyUserAskedAboutAutomaticJavaDownload) ?? false;
        var lastOfflinePlayerName = FindString(ini, KeyLastOfflinePlayerName) ?? "";
        var lastHostname = FindString(ini, KeyLastHostname) ?? "";
        var sawStateKeys = chainTotalPlayTime is not null || chainPlaytimeMigrated is not null
                           || userAskedAboutAutomaticJavaDownload
                           || lastOfflinePlayerName.Length != 0 || lastHostname.Length != 0
                           || ini.Keys.Any(JavaProbeKeys.Contains);

        // merge: per member, the playtime probe outranks the in-chain fallback, and a value
        // the launcher already recorded outranks both
        var playtimeTotal = playtime?.Total ?? chainTotalPlayTime ?? 0;
        var playtimeMigrated = playtime?.Migrated ?? chainPlaytimeMigrated ?? false;
        if (sawStateKeys || playtime is not null)
        {
            state.Update(current => current with
            {
                Playtime = new PlaytimeState
                {
                    Total = current.Playtime.Total != 0 ? current.Playtime.Total : playtimeTotal,
                    Migrated = playtimeMigrated || current.Playtime.Migrated,
                },
                UserAskedAboutAutomaticJavaDownload = userAskedAboutAutomaticJavaDownload ||
                                                      current.UserAskedAboutAutomaticJavaDownload,
                LastOfflinePlayerName = current.LastOfflinePlayerName.Length != 0
                    ? current.LastOfflinePlayerName
                    : lastOfflinePlayerName,
                Java = current.Java.LastHostname.Length == 0 && lastHostname.Length != 0
                    ? current.Java with { LastHostname = lastHostname }
                    : current.Java,
            });
        }

        // read-modify-write INSIDE one store commit: the raw payload is read under the store's
        // mutation lock, so a settings write landing mid-migration is already in the file the
        // projection reads instead of being reverted by a wholesale replace. The resolved fold
        // lands as one commit even when nothing was recognized — materializing flint.json is
        // this pass's own conclusion
        config.Update(_ => FlintConfigFold.MergeOverDefault(
            Projected(config.RawPayload() ?? FlintConfigFile.Default())));
        return new ImportResult(iniPath.Value, null);
    }

    private static long? FindLong(IniFile ini, string key) =>
        LegacyConfigProjection.Find(ini, key, []) is { } value
            ? Variant.AsLong(Variant.FromIniValue(value))
            : null;

    private static bool? FindBool(IniFile ini, string key) =>
        LegacyConfigProjection.Find(ini, key, []) is { } value
            ? Variant.AsBool(Variant.FromIniValue(value))
            : null;

    private static string? FindString(IniFile ini, string key) =>
        LegacyConfigProjection.Find(ini, key, []) is { } value
            ? Variant.AsString(Variant.FromIniValue(value))
            : null;

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

    /// <summary>The playtime pair: which of TotalPlayTime/TotalPlayTimeMigrated carried a
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
        if (ini.GetValueOrDefault("TotalPlayTime") is { ValueKind: not IniValue.Kind.Invalid } totalValue)
        {
            total = Variant.AsLong(Variant.FromIniValue(totalValue));
        }

        bool? migrated = null;
        if (ini.GetValueOrDefault("TotalPlayTimeMigrated") is { ValueKind: not IniValue.Kind.Invalid } migratedValue)
        {
            migrated = Variant.AsBool(Variant.FromIniValue(migratedValue));
        }

        return total is null && migrated is null ? null : new PlaytimePair(total, migrated);
    }
}
