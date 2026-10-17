// SPDX-License-Identifier: GPL-3.0-only
// The flint.json store (CONFIG C5): one TypedJsonFile per config root, load-or-default with
// the record-merge fold (absent members take the record property defaults), atomic writes,
// and an immediate save on every mutation (the C++ INI settings object saved per Set; the
// /settings PUT path relies on that).
//
// <see cref="Current"/> is the RESOLVED fold (defaults ← user layer; the typed instance
// layer joins the stack with D4 next phase). Storing the resolved shape keeps flint.json
// materialized and round-trip stable — with-expressions only, no reflection (AOT).
//
// Not thread-safe across instances: the host keeps exactly one; the /settings handlers
// serialize through the runtime's instance lock for mutations, reads are an atomic
// reference swap away (the same shape as LauncherStateStore).

using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.Config;

public sealed class FlintConfigStore
{
    private readonly Lock _lock = new();
    private readonly TypedJsonFile<FlintConfigFile> _file;

    /// <summary>Build (and load) the store for one flint.json. A missing file yields the
    /// record defaults; the payload is folded over a fresh default before it becomes
    /// <see cref="Current"/>.</summary>
    public FlintConfigStore(string filePath, JsonTypeInfo<FlintConfigFile> typeInfo)
    {
        _file = new TypedJsonFile<FlintConfigFile>(filePath, typeInfo);
        Current = Load();
    }

    /// <summary>The current resolved config snapshot. Treat as immutable — mutate through
    /// <see cref="Update"/>/<see cref="Set"/>.</summary>
    public FlintConfigFile Current { get; private set; }

    public string FilePath => _file.FilePath;

    /// <summary>The fresh-install default (every domain record at its property defaults).</summary>
    public static FlintConfigFile CurrentDefault() => FlintConfigFile.Default();

    /// <summary>The typed read of one key over the resolved snapshot (the adapter's
    /// RetrieveValue path). Unknown keys yield null.</summary>
    public object? Get(string key) => FlintConfigKeys.Resolve(key)?.Get(Current);

    /// <summary>Apply <paramref name="mutate"/> to the resolved snapshot and persist
    /// immediately (atomic temp+rename). Serialized internally.</summary>
    public void Update(Func<FlintConfigFile, FlintConfigFile> mutate)
    {
        lock (_lock)
        {
            Current = mutate(Current);
            _file.Save(Current);
        }
    }

    /// <summary>Typed write of one registry key (the adapter's ChangeSetting path and the
    /// importer's single pass): coerces like the C++ QVariant reads did, then persists.</summary>
    public void Set(string key, object? value)
    {
        var entry = FlintConfigKeys.Resolve(key) ??
                    throw new ArgumentException($"unknown settings key '{key}'", nameof(key));
        Update(resolved => entry.Apply(resolved, value));
    }

    /// <summary>Reset one key to its record default (the Setting.Reset path).</summary>
    public void Reset(string key) => Set(key, FlintConfigKeys.Default(key));

    /// <summary>Reload from disk (the merge + atomic-swap path; also what the ctor runs).</summary>
    private FlintConfigFile Load() =>
        _file.LoadOrDefault(CurrentDefault, FlintConfigFold.MergeOverDefault);
}
