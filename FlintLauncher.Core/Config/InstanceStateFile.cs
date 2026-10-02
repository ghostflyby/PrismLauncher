// SPDX-License-Identifier: GPL-3.0-only
// instance-state.json (DATA D4): the per-instance runtime time trio — lastLaunchTime /
// lastTimePlayed / totalTimePlayed — machine-local ("本机"), aligned with the S5
// launcher-state.json shape (one TypedJsonFile, stateVersion evolution, atomic writes).
// The C++ kept these inside instance.cfg (mixed into the settings plane); D4 splits them
// out: they are runtime data, not configuration, and never participate in the layer stack.
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.Config;

/// <summary>The instance-state.json record (D4). Milliseconds/seconds semantics match the
/// C++ exactly: LastLaunchTime is ms-since-epoch, the played durations are seconds.</summary>
public sealed record InstanceStateFile : IStateVersioned
{
    public const int CurrentVersion = 1;

    public int StateVersion { get; init; } = CurrentVersion;
    public long LastLaunchTime { get; init; }
    public long LastTimePlayed { get; init; }
    public long TotalTimePlayed { get; init; }
}

/// <summary>The instance-state.json store: one TypedJsonFile per instance directory, loaded
/// through the shared stateVersion gate (S5/D4 alignment), immediate atomic write on every
/// mutation (SetMinecraftRunning writes on every launch/stop). Not thread-safe across
/// instances; the adapter serializes per store like FlintConfigStore does.
///
/// Dual-track (D4): a Prism-format instance keeps the trio inside instance.cfg — while
/// instance-state.json does not exist, the cfg's values seed the in-memory snapshot
/// READ-ONLY (the C++ kept lastLaunchTime/totalTimePlayed in the cfg); the first mutation
/// materializes instance-state.json and the cfg's copies are ignored from then on.</summary>
public sealed class InstanceStateStore
{
    private readonly object _lock = new();
    private readonly TypedJsonFile<InstanceStateFile> _file;

    public InstanceStateStore(string instanceRoot, JsonTypeInfo<InstanceStateFile> typeInfo)
    {
        FilePath = Path.Combine(instanceRoot, "instance-state.json");
        _file = new TypedJsonFile<InstanceStateFile>(FilePath, typeInfo);
        if (File.Exists(FilePath))
        {
            Current = Load();
            return;
        }

        // the read-only seed from the legacy cfg (zeros when there is none): the C++ kept
        // the trio inside instance.cfg, and the D4 split must not lose it on a load that
        // performs no write — the first mutation materializes instance-state.json
        var legacyCfg = Path.Combine(instanceRoot, InstanceIniImporter.LegacyFileName);
        var ini = new Settings.IniFile();
        Current = ini.LoadFile(legacyCfg)
            ? new InstanceStateFile
            {
                StateVersion = InstanceStateFile.CurrentVersion,
                LastLaunchTime = Settings.Variant.AsLong(Settings.Variant.FromIniValue(
                    ini.Get("lastLaunchTime") ?? Settings.IniValue.Invalid)),
                LastTimePlayed = Settings.Variant.AsLong(Settings.Variant.FromIniValue(
                    ini.Get("lastTimePlayed") ?? Settings.IniValue.Invalid)),
                TotalTimePlayed = Settings.Variant.AsLong(Settings.Variant.FromIniValue(
                    ini.Get("totalTimePlayed") ?? Settings.IniValue.Invalid)),
            }
            : CurrentDefault();
    }

    /// <summary>The current in-memory snapshot. Treat as immutable — mutate through Update.</summary>
    public InstanceStateFile Current { get; private set; }

    public string FilePath { get; }

    public static InstanceStateFile CurrentDefault() => new() { StateVersion = InstanceStateFile.CurrentVersion };

    private InstanceStateFile Load() => _file.LoadEvolved(CurrentDefault, Evolve);

    /// <summary>Whether the state file exists yet — false means the in-memory snapshot came
    /// from the legacy cfg seed (or zeros) and nothing was persisted.</summary>
    public bool Exists => File.Exists(FilePath);

    public void Update(Func<InstanceStateFile, InstanceStateFile> mutate)
    {
        lock (_lock)
        {
            Current = mutate(Current);
            _file.Save(Current);
        }
    }

    /// <summary>The dual-track conversion half (D4): persist the current snapshot so the
    /// trio's home is instance-state.json. Called by the settings adapter on the first write
    /// of ANY kind — after the format switch the legacy cfg must feed nothing anymore.</summary>
    public void EnsureMaterialized()
    {
        if (!Exists)
        {
            Update(state => state);
        }
    }

    /// <summary>The single stateVersion gate (see LauncherStateStore.Evolve for the
    /// downgrade policy: a newer stamp stays visible instead of being papered over).</summary>
    private static InstanceStateFile Evolve(int onDiskVersion, InstanceStateFile payload) =>
        onDiskVersion < InstanceStateFile.CurrentVersion
            ? payload with { StateVersion = InstanceStateFile.CurrentVersion }
            : payload;
}
