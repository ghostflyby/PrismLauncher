// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The flint.json store: one TypedJsonFile per config root, load-or-default with
// the record-merge fold (absent members take the record property defaults), atomic writes,
// and an immediate save on every mutation (the C++ INI settings object saved per Set; the
// /settings PUT path relies on that).
//
// <see cref="Current"/> is the RESOLVED fold (defaults ← user layer; the typed instance
// layer joins the stack as the instance layer). Storing the resolved shape keeps flint.json
// materialized and round-trip stable — with-expressions only, no reflection (AOT).
//
// The store is also the Options provider of the user layer: it is the IOptionsFactory
// (every read face receives the resolved record itself — no configuration binder, no
// Activator) and the IOptionsChangeTokenSource (a token spent on every replaced snapshot)
// behind <see cref="Monitor"/>, the IOptionsMonitor consumers read through. Writes stay
// here (Update/Set/Reset); <see cref="Reload"/> adopts an external edit of the file.
//
// Not thread-safe across instances: the host keeps exactly one; mutations and reloads
// serialize on the store lock, reads are an atomic reference swap away (the same shape as
// LauncherStateStore). Change listeners run on the committing thread under that lock, in
// commit order — they must not wait on another thread's write to this store.

using System.Runtime.ExceptionServices;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.IO;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TruePath;

namespace FlintLauncher.Core.Config;

public sealed class FlintConfigStore : IOptionsFactory<FlintConfigFile>, IOptionsChangeTokenSource<FlintConfigFile>
{
    private readonly Lock _lock = new();
    private readonly TypedJsonFile<FlintConfigFile> _file;
    private readonly JsonTypeInfo<FlintConfigFile> _typeInfo;
    private CancellationTokenSource _changeSource = new();
    private byte[]? _observedPayload;

    /// <summary>Build (and load) the store for one flint.json. A missing file yields the
    /// record defaults; the payload is folded over a fresh default before it becomes
    /// <see cref="Current"/>.</summary>
    public FlintConfigStore(AbsolutePath filePath, JsonTypeInfo<FlintConfigFile> typeInfo)
    {
        _file = new TypedJsonFile<FlintConfigFile>(filePath, typeInfo);
        _typeInfo = typeInfo;
        Current = Load();
        Monitor = new OptionsMonitor<FlintConfigFile>(this, [this], new OptionsCache<FlintConfigFile>());
    }

    /// <summary>The current resolved config snapshot. Treat as immutable — mutate through
    /// <see cref="Update"/>/<see cref="Set(string, object?)"/>.</summary>
    public FlintConfigFile Current { get; private set; }

    /// <summary>The Options read face over this store: <c>CurrentValue</c> is the snapshot
    /// as of the last commit or reload, <c>OnChange</c> hears every one of them. Consumers
    /// that only read depend on this (or, from the container, <c>IOptionsSnapshot</c>);
    /// the writers keep the store. <c>IOptions</c> freezes its first snapshot for the
    /// provider lifetime by framework contract — depend on the monitor for anything that
    /// must follow changes.</summary>
    public IOptionsMonitor<FlintConfigFile> Monitor { get; }

    public AbsolutePath FilePath => _file.FilePath;

    /// <summary>The raw on-disk payload, absent members null (no fold): what a migration
    /// projects onto — everything else reads the resolved <see cref="Current"/>. A
    /// missing/unreadable/corrupt file yields null (the caller seeds).</summary>
    internal FlintConfigFile? RawPayload() => _file.LoadOrNull();

    /// <summary>The fresh-install default (every domain record at its property defaults).</summary>
    public static FlintConfigFile CurrentDefault() => FlintConfigFile.Default();

    /// <summary>The typed read of one key over the resolved snapshot (the adapter's
    /// RetrieveValue path). Unknown keys yield null.</summary>
    public object? Get(string key) => FlintConfigKeys.Resolve(key)?.Get(Current);

    /// <summary>Apply <paramref name="mutate"/> to the resolved snapshot and persist
    /// immediately (atomic temp+rename). Serialized internally; the change faces hear the
    /// replaced snapshot before this returns.</summary>
    public void Update(Func<FlintConfigFile, FlintConfigFile> mutate)
    {
        lock (_lock)
        {
            Current = mutate(Current);
            Exception? persistFailure = null;
            try
            {
                _file.Save(Current);
                // the payload just put on disk is the last bytes known to be there: the
                // watcher echo of this save is suppressed at the fingerprint, and a later
                // external restore of OLDER bytes still differs and is adopted
                _observedPayload = JsonSerializer.SerializeToUtf8Bytes(Current, _typeInfo);
            }
            catch (Exception e)
            {
                persistFailure = e;
            }

            // readers already observe the replaced snapshot (a failed save does not roll it
            // back), so the change faces must hear it too; when the save failed, a listener
            // fault must not displace the persist failure — both surface, the failure first
            try
            {
                RaiseChanged();
            }
            catch (Exception listenerFailure) when (persistFailure is not null)
            {
                persistFailure = new AggregateException(persistFailure, listenerFailure);
            }

            if (persistFailure is { } failure)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
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

    /// <summary>Typed write of several registry keys as ONE commit: one atomic save, one
    /// change notification (the /settings PUT writes a whole group this way, so a listener
    /// never applies a half-written proxy tuple). Every key resolves before anything is
    /// applied — an unknown key refuses the whole write; an empty batch writes nothing.</summary>
    public void Set(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var writes = values
            .Select(pair => (
                Entry: FlintConfigKeys.Resolve(pair.Key) ??
                       throw new ArgumentException($"unknown settings key '{pair.Key}'", nameof(values)),
                pair.Value))
            .ToArray();
        if (writes.Length == 0)
        {
            return;
        }

        Update(resolved => writes.Aggregate(resolved,
            (current, write) => write.Entry.Apply(current, write.Value)));
    }

    /// <summary>Reset one key to its record default (the Setting.Reset path).</summary>
    public void Reset(string key) => Set(key, FlintConfigKeys.Default(key));

    /// <summary>Adopts an external edit of the file: re-reads it and, when the resolved
    /// content differs, replaces the snapshot and notifies. Returns whether it did. A
    /// missing, unreadable, corrupt (the payload is kept as evidence, like a startup load)
    /// or merely re-formatted file changes nothing and fires nothing. The raw payload is
    /// fingerprinted first, so a persistently corrupt file is not re-read on the strength of
    /// its own evidence copy — that copy is a change in the watched directory too — and our
    /// own saves, which the watcher reports as well, resolve to the current snapshot.</summary>
    public bool Reload()
    {
        lock (_lock)
        {
            byte[] payload;
            try
            {
                payload = File.ReadAllBytes(FilePath.Value);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or SecurityException or NotSupportedException)
            {
                return false;
            }

            if (_observedPayload is { } seen && seen.AsSpan().SequenceEqual(payload))
            {
                return false;
            }

            _observedPayload = payload;
            if (_file.LoadOrNull() is not { } loaded)
            {
                return false;
            }

            var resolved = FlintConfigFold.MergeOverDefault(loaded);
            if (SameContent(resolved, Current))
            {
                return false;
            }

            Current = resolved;
            RaiseChanged();
            return true;
        }
    }

    /// <summary>Opts the store into the watcher-driven live model: an external edit of the
    /// file (an editor, a sync client) reloads through the space's serialized reconcile
    /// instead of waiting for the next process start. The watched root is the file's
    /// directory — the atomic save's temp files and the groups file raise there too, and
    /// those raises resolve to the current snapshot.</summary>
    public void AttachWatcher(DirectorySpace space)
    {
        var directory = FilePath.Parent
                        ?? throw new InvalidOperationException(
                            $"config file '{FilePath.Value}' sits at the filesystem root");
        var root = MaterializedDirectory.Ensure(directory)
                   ?? throw new InvalidOperationException(
                       $"cannot create the config directory '{directory.Value}'");
        space.Register(root, () => Reload());
    }

    /// <summary>The change token of the current snapshot: fires once it is replaced (a
    /// write, or a reload that found different content), after which a fresh token stands
    /// for the next change — the ChangeToken.OnChange shape the Options monitor rides.</summary>
    public IChangeToken GetChangeToken() =>
        new CancellationChangeToken(Volatile.Read(ref _changeSource).Token);

    string? IOptionsChangeTokenSource<FlintConfigFile>.Name => Options.DefaultName;

    // The factory face: the resolved snapshot itself, for every read face (IOptions,
    // IOptionsSnapshot, the monitor). The user layer has no named variants — and the monitor
    // only ever evicts the default name, so a named snapshot would never refresh.
    FlintConfigFile IOptionsFactory<FlintConfigFile>.Create(string name)
    {
        if (name != Options.DefaultName)
        {
            throw new ArgumentException(
                $"the user config layer has no named variants (requested '{name}')", nameof(name));
        }

        return Current;
    }

    /// <summary>Load from disk (the merge + atomic-swap path the ctor runs).</summary>
    private FlintConfigFile Load() =>
        _file.LoadOrDefault(CurrentDefault, FlintConfigFold.MergeOverDefault);

    // called under the lock: the spent token's callbacks (the monitor's eviction and its
    // listeners) run right here, so notifications arrive in commit order
    private void RaiseChanged()
    {
        var spent = Interlocked.Exchange(ref _changeSource, new CancellationTokenSource());
        spent.Cancel();
    }

    // record equality cannot serve here (EnvMap is a dictionary — reference equality), the
    // serialized form is the identity flint.json round-trips on anyway
    private bool SameContent(FlintConfigFile left, FlintConfigFile right) =>
        JsonSerializer.SerializeToUtf8Bytes(left, _typeInfo).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, _typeInfo));
}
