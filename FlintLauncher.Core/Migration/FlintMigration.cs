// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Globalization;
using System.Security;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.State;
using TruePath;

namespace FlintLauncher.Core.Migration;

/// <summary>The manual flint migration: what is pending, and the one run that clears it.</summary>
public static class FlintMigration
{
    /// <summary>The decision-record source of the config projection half (the remount half
    /// files under the flint-legacy profile; the instance half needs no record — a converted
    /// directory carries instance.json and never converts again).</summary>
    public const string ConfigSourceName = "flint-config";

    /// <summary>What a run would do. Each half is pending on its own signal: the remount on
    /// an undecided legacy layout, the config projection on an undecided INI chain file, the
    /// instance conversion on any cfg-only instance directory.</summary>
    public sealed record Candidates(
        AbsolutePath? LegacyRoot,
        bool RemountDecided,
        AbsolutePath? IniChainFile,
        int PendingInstances)
    {
        public bool Any =>
            (LegacyRoot is not null && !RemountDecided) || IniChainFile is not null || PendingInstances > 0;
    }

    /// <summary>What the run did (one entry per half; a half that threw reports its idle
    /// outcome — the next run retries it).</summary>
    public sealed record Result(
        FlintLegacyMigration.Result Remount,
        FlintConfigImporter.ImportResult Config,
        LegacyInstanceConversion.Result Instances)
    {
        public static readonly Result None = new(
            FlintLegacyMigration.Result.Skipped, FlintConfigImporter.ImportResult.Idle,
            LegacyInstanceConversion.Result.None);
    }

    public static Candidates Detect(FlintPaths paths, MigrationDecisionStore? decisions,
        IEnumerable<AbsolutePath> instanceRoots) => new(
        FlintLegacyMigrationProfile.LegacySourceRoot(paths),
        decisions?.Read(FlintLegacyMigrationProfile.ProfileName) is not null,
        decisions?.Read(ConfigSourceName) is null ? FlintConfigImporter.FindChainFile(paths) : null,
        LegacyInstanceConversion.Pending(instanceRoots).Count);

    /// <summary>Run every half, in dependency order (the projection probes the config root
    /// the remount fills). A half's failure never stops the others; the projection and the
    /// remount seal their decision records only over a pass that completed.</summary>
    public static Result RunAll(FlintPaths paths, MigrationDecisionStore? decisions,
        FlintConfigStore config, ISecretStore secrets, LauncherStateStore state,
        IEnumerable<AbsolutePath> instanceRoots, JsonTypeInfo<InstanceConfigFile> configInfo,
        JsonTypeInfo<InstanceStateFile> stateInfo)
    {
        var remount = TryHalf(() => FlintLegacyMigration.ExecuteIfNeeded(paths, decisions),
            FlintLegacyMigration.Result.Skipped);

        var configResult = TryHalf(() =>
        {
            var import = FlintConfigImporter.Import(paths, config, secrets, state);
            if (import.ImportedIni is not null)
            {
                SealConfigDecision(decisions);
            }

            return import;
        }, FlintConfigImporter.ImportResult.Idle);

        var instances = TryHalf(() => LegacyInstanceConversion.Convert(instanceRoots, configInfo, stateInfo),
            LegacyInstanceConversion.Result.None);
        return new Result(remount, configResult, instances);
    }

    /// <summary>Seal the config projection's one-shot record over a projection that ran
    /// (RunAll and the prism import completion both seal — the chain file stays on disk, so
    /// without the record the candidate view would propose the projection forever).</summary>
    public static void SealConfigDecision(MigrationDecisionStore? decisions)
    {
        try
        {
            decisions?.Write(new MigrationDecisionRecord
            {
                Source = ConfigSourceName,
                Decision = MigrationDecisionRecord.Imported,
                At = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                Mode = "projected",
            });
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                      or SecurityException or NotSupportedException)
        {
            // a failed seal write re-proposes the pass — an honest no-op, not a failure
        }
    }

    private static T TryHalf<T>(Func<T> half, T idle) where T : class
    {
        try
        {
            return half();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or SecurityException or NotSupportedException
                                      or ArgumentException or InvalidOperationException)
        {
            // one bad half (an unreadable INI, a locked store file) must not strand the
            // others: the run reports the idle outcome for this half and continues
            return idle;
        }
    }
}
