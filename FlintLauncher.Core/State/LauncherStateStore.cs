// SPDX-License-Identifier: GPL-3.0-only
// The launcher-state.json store (STATE S5, assembly step 4): one TypedJsonFile per state
// root, load-or-default with the stateVersion evolution gate, and immediate atomic writes on
// every mutation ("变更即时原子写"). The loaded record lives in <see cref="Current"/> so the
// hot paths (java probe cache lookups, playtime accumulation) never touch the disk; writers
// go through <see cref="Update"/> which replaces the immutable snapshot and persists it.
//
// The JsonTypeInfo comes from the host's source-generated context (FlintJsonContext —
// GLOBAL §1 keeps the registration list in one place); Core only ever sees the opaque
// type-info handle, so no reflection ever enters the picture (AOT red line).

using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.State;

public sealed class LauncherStateStore
{
    private readonly Lock _lock = new();
    private readonly TypedJsonFile<LauncherState> _file;

    /// <summary>Build (and load) the store for one state file. A missing file yields the
    /// default; the on-disk record is folded over a fresh default (see <see cref="Load"/>)
    /// and version-gated before it becomes <see cref="Current"/>.</summary>
    public LauncherStateStore(string filePath, JsonTypeInfo<LauncherState> typeInfo)
    {
        _file = new TypedJsonFile<LauncherState>(filePath, typeInfo);
        Current = Load();
    }

    /// <summary>The current in-memory snapshot. Treat as immutable — mutate through
    /// <see cref="Update"/>; the reference swap is atomic and the sub-records are never
    /// edited in place.</summary>
    public LauncherState Current { get; private set; }

    public string FilePath => _file.FilePath;

    /// <summary>The fresh-install default (S5 schema version 1).</summary>
    public static LauncherState CurrentDefault() => new() { StateVersion = LauncherState.CurrentVersion };

    /// <summary>Reload from disk (the record-merge + version-gate path; also what the
    /// constructor runs). Goes through the shared <see cref="TypedJsonFileVersioning.LoadEvolved"/>
    /// gate — the single stateVersion implementation (S5/D4 alignment): the merge folds the
    /// payload over the current defaults first (the source-generated deserializer does NOT
    /// honor property initializers for members absent from the payload — an older or partial
    /// file would otherwise surface null halves), then the gate hands differing versions to
    /// <see cref="Evolve"/>. Fluent's own writes always round-trip every member; the fold is
    /// the defense for older-schema payloads and hand-edited files.</summary>
    private LauncherState Load() =>
        _file.LoadEvolved(CurrentDefault, Evolve, MergeOverDefault);

    /// <summary>The fold: every absent half/member takes the current default.</summary>
    internal static LauncherState MergeOverDefault(LauncherState merged)
    {
        var fresh = CurrentDefault();
        return merged with
        {
            LastOfflinePlayerName = merged.LastOfflinePlayerName ?? fresh.LastOfflinePlayerName,
            Playtime = merged.Playtime ?? fresh.Playtime,
            Java = merged.Java is null ? fresh.Java : NormalizeJava(merged.Java),
            Ui = merged.Ui is null ? fresh.Ui : NormalizeUi(merged.Ui),
        };
    }

    private static JavaDetectState NormalizeJava(JavaDetectState java) => java with
    {
        LastHostname = java.LastHostname ?? "",
        Detections = java.Detections is null
            ? new Dictionary<string, JavaDetectEntry>(StringComparer.Ordinal)
            : new Dictionary<string, JavaDetectEntry>(
                java.Detections.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value is null
                        ? new JavaDetectEntry("", "", "", "", "")
                        : pair.Value with
                        {
                            Signature = pair.Value.Signature ?? "",
                            Architecture = pair.Value.Architecture ?? "",
                            RealArchitecture = pair.Value.RealArchitecture ?? "",
                            Version = pair.Value.Version ?? "",
                            Vendor = pair.Value.Vendor ?? "",
                        }),
                StringComparer.Ordinal),
    };

    private static UiState NormalizeUi(UiState ui) => ui with
    {
        HiddenGroups = ui.HiddenGroups ?? [],
    };

    /// <summary>Apply <paramref name="mutate"/> to the snapshot and persist immediately
    /// (atomic temp+rename). Serialized internally — launch-pipeline writers and API-driven
    /// writers may interleave.</summary>
    public void Update(Func<LauncherState, LauncherState> mutate)
    {
        lock (_lock)
        {
            Current = mutate(Current);
            _file.Save(Current);
        }
    }

    /// <summary>Evolution gate. An OLDER payload is upgraded: the merge already folded the
    /// missing halves over the current defaults, so stamping the current version is truthful.
    /// A NEWER payload (downgrade — an older binary reading a newer state file) keeps its
    /// on-disk stamp: the source-generated deserializer has ALREADY dropped the members this
    /// build doesn't know at load time, and rewriting the file stamped as the current version
    /// would additionally falsify the schema a future binary expects. The stamp staying at
    /// the newer version keeps that damage visible instead of papering over it.</summary>
    private static LauncherState Evolve(int onDiskVersion, LauncherState payload) =>
        onDiskVersion < LauncherState.CurrentVersion
            ? payload with { StateVersion = LauncherState.CurrentVersion }
            : payload;
}
