// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.IO;

/// <summary>One coalescing watch over a directory tree. Raises its reconcile callback
/// at most once per event burst (debounce), and again when
/// the watched root returns after being deleted or moved away.
/// <para>Consumers re-read the tree on every raise — a raise only means "reconcile
/// now", never "here is what changed". A moved root is followed: the watch re-points at
/// the new location and reports the move through <see cref="Moved"/>. On platforms
/// without rename-following, recovery waits for the root to reappear at the registered
/// path.</para>
/// <para><see cref="Pulse"/> raises synchronously — tests and embedding hosts drive the
/// pipeline deterministically instead of waiting on real watcher timing.</para></summary>
public sealed class DirectoryWatch : IDisposable
{
    private const int DebounceMsDefault = 200;
    private const int RetryMinMs = 1000;
    private const int RetryMaxMs = 16000;

    private MaterializedDirectory _root;
    private AbsolutePath _currentPath;
    private DirectoryAnchor? _anchor;
    private readonly Action _onChanged;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();

    private FileSystemWatcher? _fsw;
    private Timer? _debounceTimer;
    private Timer? _retryTimer;
    private int _retryDelayMs = RetryMinMs;
    private bool _rootLost;
    private bool _disposed;

    /// <summary>Binds the watch. The tree is watched from the constructor on — raises can
    /// arrive before it returns.</summary>
    /// <param name="root">The directory to watch (must exist — the injection sites
    /// materialize it).</param>
    /// <param name="onChanged">The reconcile callback, invoked on debounce coalescence,
    /// on loss/recovery, and from <see cref="Pulse"/>.</param>
    /// <param name="debounce">The burst-coalescing window; null takes the 200ms default.</param>
    public DirectoryWatch(MaterializedDirectory root, Action onChanged, TimeSpan? debounce = null)
    {
        _root = root;
        _currentPath = root.Path;
        _onChanged = onChanged;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(DebounceMsDefault);
        Bind();
    }

    /// <summary>Where the watch is currently pointed (follows renames/moves detected
    /// through the anchor).</summary>
    public AbsolutePath Root => _currentPath;

    /// <summary>The watched root was renamed/moved elsewhere on disk: (old root, new
    /// root). The watch re-points itself at the new location; DirectorySpace re-keys and
    /// tenants re-point through this.</summary>
    public event Action<AbsolutePath, AbsolutePath>? Moved;

    /// <summary>Whether the watcher is currently bound to a live root (false while the
    /// root is missing and re-binding is backing off).</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return !_rootLost && _fsw is not null;
            }
        }
    }

    /// <summary>The synchronous reconcile hook: fires onChanged immediately, bypassing the
    /// debounce. Tests drive the pipeline through this.</summary>
    public void Pulse()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _onChanged();
    }

    /// <summary>Re-binds immediately when the root exists again (else schedules the next
    /// backoff attempt). The tests' deterministic counterpart of the retry timer. A rename
    /// the platform never reported (linux inotify keeps delivering on the renamed inode,
    /// so neither an Error nor a root-Deleted arrives) is caught here: a rearm that finds
    /// the root gone takes the lost state, and the anchor in TryRebind re-derives the
    /// move.</summary>
    public void Rearm()
    {
        var lostNow = false;
        lock (_gate)
        {
            if (!_disposed && !_rootLost && !_currentPath.ExistsDirectory())
            {
                _rootLost = true;
                _fsw?.Dispose();
                _fsw = null;
                lostNow = true;
            }
        }

        if (lostNow)
        {
            FireChanged(); // tenants orphan now (the same notice a reported loss fires)
        }

        TryRebind();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _retryTimer?.Dispose();
            _retryTimer = null;
            _fsw?.Dispose();
            _fsw = null;
            _anchor?.Dispose();
            _anchor = null;
        }
    }

    private void Bind()
    {
        var fsw = new FileSystemWatcher(_currentPath.Value)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                                  | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        fsw.Created += (_, e) => MarkDirtyOrRootLoss(e.FullPath);
        fsw.Changed += (_, _) => MarkDirty();
        fsw.Deleted += (_, e) => MarkDirtyOrRootLoss(e.FullPath);
        fsw.Renamed += (_, _) => MarkDirty();
        fsw.Error += (_, _) => RootLost();
        fsw.EnableRaisingEvents = true;

        // the anchor pins the directory identity at the bound path, so a renamed/moved
        // root is re-found through it; where the anchor is unavailable the path-based
        // backoff below stays the only recovery story
        _anchor?.Dispose();
        _anchor = DirectoryAnchor.TryAcquire(_currentPath);
        _fsw = fsw;
        _rootLost = false;
        _retryDelayMs = RetryMinMs;
    }

    private void MarkDirty() => ScheduleFire();

    private void MarkDirtyOrRootLoss(string fullPath)
    {
        // some platforms deliver the root's own deletion as a Deleted event before (or
        // instead of) an Error; both routes end in the same handled state
        if (string.Equals(fullPath, _currentPath.Value, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            RootLost();
            return;
        }

        MarkDirty();
    }

    private void ScheduleFire()
    {
        lock (_gate)
        {
            if (_disposed || _rootLost)
            {
                return;
            }

            if (_debounceTimer is null)
            {
                _debounceTimer = new Timer(FireChanged, null, _debounce, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void FireChanged(object? state = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        try
        {
            _onChanged();
        }
        catch
        {
            // a broken reconcile must not kill the timer thread; the dispatcher (which
            // owns logging and locking) is the place to surface failures
        }
    }

    private void RootLost()
    {
        lock (_gate)
        {
            if (_disposed || _rootLost)
            {
                return;
            }

            _rootLost = true;
            _fsw?.Dispose();
            _fsw = null;
        }

        FireChanged(); // tenants orphan now
        ScheduleRetry(RetryMinMs);
    }

    private void ScheduleRetry(int delayMs)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _retryTimer?.Dispose();
            _retryTimer = new Timer(_ => TryRebind(), null, TimeSpan.FromMilliseconds(delayMs),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void TryRebind()
    {
        lock (_gate)
        {
            if (_disposed || !_rootLost)
            {
                return;
            }
        }

        try
        {
            // anchor-first: a renamed/moved root is re-found at its new location and the
            // watch re-points there (the Moved event lets the space re-key and tenants
            // re-point their own state); the old path staying missing means deleted — the
            // registered-path check below still catches a same-path recreation
            if (_anchor is { } anchor && anchor.TryCurrentPath() is { } movedTo && movedTo.ExistsDirectory())
            {
                var old = _currentPath;
                RebindAt(movedTo);
                Moved?.Invoke(old, movedTo);
                FireChanged();
                return;
            }

            lock (_gate)
            {
                if (_disposed || !_rootLost)
                {
                    return;
                }

                if (!_currentPath.ExistsDirectory())
                {
                    _retryDelayMs = Math.Min(_retryDelayMs * 2, RetryMaxMs);
                    ScheduleRetry(_retryDelayMs);
                    return;
                }

                Bind();
            }

            FireChanged(); // the root returned — tenants re-adopt
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // a bind that lost the TOCTOU race (or was refused by the platform) must not
            // escape onto the retry timer thread — fall back to the lost state and keep
            // backing off
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _fsw?.Dispose();
                _fsw = null;
                _rootLost = true;
            }

            ScheduleRetry(_retryDelayMs);
        }
    }

    private void RebindAt(AbsolutePath newPath)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _fsw?.Dispose();
            _fsw = null;
            _currentPath = newPath;
            _root = MaterializedDirectory.Wrap(newPath) ?? _root;
            _rootLost = false;
            _retryDelayMs = RetryMinMs;
        }

        Bind();
    }
}
