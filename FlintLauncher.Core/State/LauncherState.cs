// SPDX-License-Identifier: GPL-3.0-only
// The launcher-state.json schema (STATE S5): the machine-local runtime state the C++ used to
// disguise as settings keys (CONFIG C9: TotalPlayTime*/LastHostname/Java probe
// quintet/UserAskedAboutAutomaticJavaDownload/LastOfflinePlayerName) plus the local group
// collapse state split out of instgroups.json (CONFIG C8 hidden half). Strongly-typed
// records, source-generation registered in FlintJsonContext (AOT red line), persisted by
// <see cref="LauncherStateStore"/> with immediate atomic writes — no layering: everything
// here is process/machine-level (STATE S6).

namespace FlintLauncher.Core.State;

/// <summary>The single runtime-state file (S5). The record IS the schema version: new
/// members take their property defaults through the record-merge load, and the
/// <see cref="IStateVersioned.StateVersion"/> gate drives evolution (GLOBAL §1).</summary>
public sealed record LauncherState : IStateVersioned
{
    /// <summary>The current schema version (S5 starts at 1).</summary>
    public const int CurrentVersion = 1;

    public required int StateVersion { get; init; }

    /// <summary>Global playtime bookkeeping (the former TotalPlayTime/TotalPlayTimeMigrated
    /// pseudo-config keys, C9).</summary>
    public PlaytimeState Playtime { get; init; } = new();

    /// <summary>The one-shot automatic-java-download consent ask (C9).</summary>
    public bool UserAskedAboutAutomaticJavaDownload { get; init; }

    /// <summary>The offline-launch name suggestion (C9; LaunchController writes it back).</summary>
    public string LastOfflinePlayerName { get; init; } = "";

    /// <summary>Java detection bookkeeping: the hostname rescan anchor plus the per-binary
    /// probe cache (the former JavaSignature/Architecture/RealArchitecture/Version/Vendor
    /// global keys, now path-keyed — S5 fixes the five-keys-overwrite-each-other defect
    /// where multiple JVMs on one machine only remembered the last probe).</summary>
    public JavaDetectState Java { get; init; } = new();

    /// <summary>Local group view state (the instgroups.json "hidden" half, C8).</summary>
    public UiState Ui { get; init; } = new();
}

/// <summary>Global playtime accumulation state (S5 PlaytimeState).</summary>
public sealed record PlaytimeState
{
    /// <summary>Total seconds played across all instances (former TotalPlayTime).</summary>
    public long Total { get; init; }

    /// <summary>Whether the one-shot instance-total migration already ran
    /// (former TotalPlayTimeMigrated).</summary>
    public bool Migrated { get; init; }
}

/// <summary>Java detection state (S5): the hostname anchor plus the probe cache keyed by the
/// java executable path (same path ⇒ no rescan; different paths no longer overwrite each
/// other).</summary>
public sealed record JavaDetectState
{
    /// <summary>The host the last java scan ran on. SCHEMA PLACEHOLDER in this port: the
    /// C++ Application compared it against the current hostname to force a rescan, but that
    /// consumer was never ported — nothing reads or writes it yet (STATE S5 keeps the field
    /// so the schema is stable when the rescan logic lands).</summary>
    public string LastHostname { get; init; } = "";

    /// <summary>Probe results keyed by the resolved java executable path.</summary>
    public Dictionary<string, JavaDetectEntry> Detections { get; init; } = new();
}

/// <summary>One probed java binary (CheckJava writes it after a successful JdkFind probe).</summary>
public sealed record JavaDetectEntry(
    string Signature,
    string Architecture,
    string RealArchitecture,
    string Version,
    string Vendor);

/// <summary>The machine-local group view state (C8 hidden half; G4 keeps group NAMES as the
/// keys — no group IDs). The empty-string group is represented by
/// <see cref="UngroupedHidden"/>, not by a list entry.</summary>
public sealed record UiState
{
    /// <summary>Collapsed (hidden) group names.</summary>
    public IReadOnlyList<string> HiddenGroups { get; init; } = [];

    /// <summary>Whether the ungrouped pseudo-group is collapsed.</summary>
    public bool UngroupedHidden { get; init; }
}
