// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using JdkFind;

namespace FlintLauncher.Core.State;

/// <summary>The single runtime-state file. The record IS the schema version: new
/// members take their property defaults through the record-merge load, and the
/// <see cref="IStateVersioned.StateVersion"/> gate drives evolution.</summary>
public sealed record LauncherState : IStateVersioned
{
    /// <summary>The current schema version (starts at 1).</summary>
    public const int CurrentVersion = 1;

    // The materializing constructor: the source-generated deserializer does not honor
    // property initializers for members absent from the payload, so the ctor body
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

    /// <summary>Per-binary java probe cache, keyed by the executable path.</summary>
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

/// <summary>Probe results keyed by the resolved java executable path.</summary>
public sealed record JavaDetectState
{
    public Dictionary<string, JavaDetectEntry> Detections { get; init; } = new();
}

/// <summary>One probed java binary. <see cref="Signature"/> is the launcher's file-mtime +
/// path invalidation stamp; the probe result is the JdkFind <see cref="JavaExecutable"/>
/// itself.</summary>
[JsonConverter(typeof(JavaDetectEntryJsonConverter))]
public sealed record JavaDetectEntry(string Signature, JavaExecutable Executable)
{
    /// <summary>The Java SE feature version of the backing installation; null when no release
    /// file backs the binary, which CheckJava treats as a stale probe.</summary>
    [JsonIgnore]
    public int? LanguageVersion => Executable.Installation?.LanguageVersion;
}

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
