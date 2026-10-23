// SPDX-License-Identifier: GPL-3.0-only
// The launcher-state.json schema: the machine-local runtime state the C++ used to
// disguise as settings keys (TotalPlayTime*/LastHostname/Java probe
// quintet/UserAskedAboutAutomaticJavaDownload/LastOfflinePlayerName) plus the local group
// collapse state split out of instgroups.json. Strongly-typed
// records, source-generation registered in FlintJsonContext (AOT red line), persisted by
// <see cref="LauncherStateStore"/> with immediate atomic writes — no layering: everything
// here is process/machine-level.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace FlintLauncher.Core.State;

/// <summary>The single runtime-state file. The record IS the schema version: new
/// members take their property defaults through the record-merge load, and the
/// <see cref="IStateVersioned.StateVersion"/> gate drives evolution.</summary>
public sealed record LauncherState : IStateVersioned
{
    /// <summary>The current schema version (starts at 1).</summary>
    public const int CurrentVersion = 1;

    // The materializing constructor: the source-generated deserializer does not honor
    // property initializers for members absent from the payload (.NET 10), so the ctor body
    // re-materializes the sub-records — every deserialized LauncherState has real domain
    // values, and LauncherStateStore's fold only normalizes inner scalars.
    [JsonConstructor, SetsRequiredMembers]
    public LauncherState(int stateVersion = CurrentVersion,
        bool userAskedAboutAutomaticJavaDownload = false, string lastOfflinePlayerName = "",
        PlaytimeState? playtime = null, JavaDetectState? java = null, UiState? ui = null)
    {
        StateVersion = stateVersion;
        UserAskedAboutAutomaticJavaDownload = userAskedAboutAutomaticJavaDownload;
        LastOfflinePlayerName = lastOfflinePlayerName;
        Playtime = playtime ?? new();
        Java = java ?? new();
        Ui = ui ?? new();
    }

    public required int StateVersion { get; init; }

    /// <summary>Global playtime bookkeeping (the former TotalPlayTime/TotalPlayTimeMigrated
    /// pseudo-config keys).</summary>
    public PlaytimeState Playtime { get; init; } = new();

    /// <summary>The one-shot automatic-java-download consent ask.</summary>
    public bool UserAskedAboutAutomaticJavaDownload { get; init; }

    /// <summary>The offline-launch name suggestion (LaunchController writes it back).</summary>
    public string LastOfflinePlayerName { get; init; } = "";

    /// <summary>Java detection bookkeeping: the hostname rescan anchor plus the per-binary
    /// probe cache (the former JavaSignature/Architecture/RealArchitecture/Version/Vendor
    /// global keys, now path-keyed — fixing the five-keys-overwrite-each-other defect
    /// where multiple JVMs on one machine only remembered the last probe).</summary>
    public JavaDetectState Java { get; init; } = new();

    /// <summary>Local group view state (the instgroups.json "hidden" half).</summary>
    public UiState Ui { get; init; } = new();
}

/// <summary>Global playtime accumulation state.</summary>
public sealed record PlaytimeState
{
    /// <summary>Total seconds played across all instances (former TotalPlayTime).</summary>
    public long Total { get; init; }

    /// <summary>Whether the one-shot instance-total migration already ran
    /// (former TotalPlayTimeMigrated).</summary>
    public bool Migrated { get; init; }
}

/// <summary>Java detection state: the hostname anchor plus the probe cache keyed by the
/// java executable path (same path ⇒ no rescan; different paths no longer overwrite each
/// other).</summary>
public sealed record JavaDetectState
{
    /// <summary>The host the last java scan ran on. SCHEMA PLACEHOLDER in this port: the
    /// C++ Application compared it against the current hostname to force a rescan, but that
    /// consumer was never ported — nothing reads or writes it yet (the field is kept
    /// so the schema is stable when the rescan logic lands).</summary>
    public string LastHostname { get; init; } = "";

    /// <summary>Probe results keyed by the resolved java executable path.</summary>
    public Dictionary<string, JavaDetectEntry> Detections { get; init; } = new();
}

/// <summary>One probed java binary (CheckJava writes it after a successful JdkFind probe).
/// <see cref="LanguageVersion"/> is the Java SE feature version of the backing installation;
/// null when JdkFind found no release file to read it from — and in entries written before
/// the member existed, which CheckJava treats as a stale probe.</summary>
public sealed record JavaDetectEntry(
    string Signature,
    string Architecture,
    string RealArchitecture,
    string Version,
    string Vendor,
    int? LanguageVersion = null);

/// <summary>The machine-local group view state (the instgroups.json "hidden" half; group NAMES stay the
/// keys — no group IDs). The empty-string group is represented by
/// <see cref="UngroupedHidden"/>, not by a list entry.</summary>
public sealed record UiState
{
    /// <summary>Collapsed (hidden) group names.</summary>
    public IReadOnlyList<string> HiddenGroups { get; init; } = [];

    /// <summary>Whether the ungrouped pseudo-group is collapsed.</summary>
    public bool UngroupedHidden { get; init; }
}
