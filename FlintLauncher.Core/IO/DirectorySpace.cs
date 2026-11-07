// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.IO;

/// <summary>A registry of watched directory roots. Register the directories a model
/// owns; the space raises the model's reconcile when its tree changes externally, and
/// serializes every reconcile and moved notification through the dispatcher given at
/// construction.
/// <para>Callbacks arrive on watcher threads unless a dispatcher re-marshalles them —
/// consumers that own mutable state pass their lock here.</para>
/// <para><see cref="Pulse"/> reconciles synchronously (deterministic tests and embedding
/// hosts); <see cref="RearmAll"/> re-binds watches whose roots came back;
/// <see cref="Move"/> renames a registered root and announces it exactly like an
/// external rename.</para></summary>
public sealed class DirectorySpace : IDisposable
{
    private sealed record Tenant(DirectoryWatch Watch, Action<AbsolutePath, AbsolutePath>? Moved);

    private readonly Dictionary<AbsolutePath, Tenant> _watches = new();
    private readonly Action<Action>? _dispatch;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Runs every reconcile callback. The composition root passes its
    /// lock/serialization here.</summary>
    public DirectorySpace(Action<Action>? reconcileDispatcher = null) => _dispatch = reconcileDispatcher;

    /// <summary>Raised after a registered root was renamed or moved elsewhere on disk:
    /// (old root, new root). Consumers re-point their own state to the new root
    /// here.</summary>
    public event Action<AbsolutePath, AbsolutePath>? TenantMoved;

    /// <summary>Registers (idempotent per root) a reconcile callback fired on coalesced
    /// external changes to the root's tree. The callback must be cheap to re-run — it is
    /// a full reconcile, not a delta.
    /// <para><paramref name="moved"/>, when given, runs after the watch re-pointed itself
    /// at a renamed or moved root — re-point your own state there.</para></summary>
    public void Register(MaterializedDirectory root, Action reconcile,
        Action<AbsolutePath, AbsolutePath>? moved = null)
    {
        lock (_gate)
        {
            if (_disposed || _watches.ContainsKey(root.Path))
            {
                return;
            }

            var watch = new DirectoryWatch(root, () => RunReconcile(reconcile));
            var tenant = new Tenant(watch, moved);
            watch.Moved += (oldRoot, newRoot) => NotifyMoved(tenant, oldRoot, newRoot);
            _watches[root.Path] = tenant;
        }
    }

    /// <summary>Re-keys the registration, then announces the move through the dispatcher
    /// (the same serialization the reconciles use — consumers re-point their state
    /// without racing them): <see cref="TenantMoved"/> plus the tenant's moved callback.
    /// A failing announcement is swallowed like a failing reconcile — it must not kill
    /// the watch's timer thread; the next change re-runs the pipeline.</summary>
    private void NotifyMoved(Tenant tenant, AbsolutePath oldRoot, AbsolutePath newRoot)
    {
        Rekey(oldRoot, newRoot);
        var dispatch = _dispatch ?? (action => action());
        try
        {
            dispatch(() =>
            {
                TenantMoved?.Invoke(oldRoot, newRoot);
                tenant.Moved?.Invoke(oldRoot, newRoot);
            });
        }
        catch
        {
            // the dispatcher owns logging; the tenant's next reconcile re-reads reality
        }
    }

    /// <summary>Whether a root is currently registered (tests and re-point logic query
    /// this instead of guessing from callbacks).</summary>
    public bool IsRegistered(AbsolutePath root)
    {
        lock (_gate)
        {
            return _watches.ContainsKey(root);
        }
    }

    /// <summary>Cooperatively moves a registered root: renames it to
    /// <paramref name="to"/> (same-volume rename, atomic), re-keys the registration,
    /// re-points the watch and announces the move exactly like an external rename —
    /// <see cref="TenantMoved"/> plus the tenant's moved callback — then reconciles once,
    /// so every tenant is consistent at the new location when this returns.
    /// Returns false when <paramref name="from"/> is not a registered root, the target
    /// exists, or the move failed (cross-volume moves are the caller's FileOps
    /// concern — no current tenant moves across volumes). Must not be called while
    /// holding the dispatcher's lock — the announcement goes through it.</summary>
    public bool Move(AbsolutePath from, AbsolutePath to)
    {
        Tenant? tenant;
        lock (_gate)
        {
            if (_disposed || !_watches.TryGetValue(from, out tenant) || _watches.ContainsKey(to)
                || !from.ExistsDirectory() || to.ExistsDirectory())
            {
                return false;
            }
        }

        try
        {
            Directory.Move(from.Value, to.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        NotifyMoved(tenant, from, to);
        tenant.Watch.Pulse();
        return true;
    }

    private void Rekey(AbsolutePath oldRoot, AbsolutePath newRoot)
    {
        lock (_gate)
        {
            if (_watches.Remove(oldRoot, out var tenant))
            {
                _watches[newRoot] = tenant;
            }
        }
    }

    public void Unregister(AbsolutePath root)
    {
        lock (_gate)
        {
            if (_watches.Remove(root, out var tenant))
            {
                tenant.Watch.Dispose();
            }
        }
    }

    /// <summary>Diffs the tenant's root set onto the watch set: roots that left are
    /// unwatched, roots that arrived are wrapped and watched. The reconcile callback is
    /// shared by all roots (it re-reads the whole tenancy).</summary>
    public void SyncTenantRoots(IEnumerable<AbsolutePath> oldRoots, IEnumerable<AbsolutePath> newRoots,
        Action reconcile)
    {
        // materialize FIRST: the sequences are enumerated exactly once (a lazy caller's
        // second pass may differ from its first, which would corrupt the diff)
        var oldList = oldRoots.ToArray();
        var newList = newRoots.ToArray();
        var oldSet = oldList.ToHashSet();
        var newSet = newList.ToHashSet();

        foreach (var removed in oldList.Where(root => !newSet.Contains(root)))
        {
            Unregister(removed);
        }

        foreach (var added in newList.Where(root => !oldSet.Contains(root)))
        {
            if (MaterializedDirectory.Wrap(added) is { } materialized)
            {
                Register(materialized, reconcile);
            }
        }
    }

    /// <summary>Synchronously reconciles one root (or every registered root when null) —
    /// the deterministic test/embedding entry point.</summary>
    public void Pulse(AbsolutePath? root = null)
    {
        List<DirectoryWatch> targets;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            targets = root is { } one
                ? _watches.TryGetValue(one, out var tenant) ? [tenant.Watch] : []
                : [.. _watches.Values.Select(tenantEntry => tenantEntry.Watch)];
        }

        foreach (var watch in targets)
        {
            watch.Pulse();
        }
    }

    /// <summary>Re-binds every watch whose root exists again (the deterministic counterpart
    /// of DirectoryWatch's backoff retry timer). Must not be called while holding the
    /// dispatcher's lock — a rearm can reach user callbacks through it.</summary>
    public void RearmAll()
    {
        List<DirectoryWatch> targets;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            targets = [.. _watches.Values.Select(tenant => tenant.Watch)];
        }

        foreach (var watch in targets)
        {
            watch.Rearm();
        }
    }

    private void RunReconcile(Action reconcile)
    {
        var dispatch = _dispatch ?? (action => action());
        try
        {
            dispatch(reconcile);
        }
        catch
        {
            // a failed reconcile is retried on the next change; the dispatcher owns
            // logging and the tenant owns its IO failure semantics
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var tenant in _watches.Values)
            {
                tenant.Watch.Dispose();
            }

            _watches.Clear();
            _disposed = true;
        }
    }
}
