// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.Web.Services;

/// <summary>Serializes the instance-list mutations (create/delete/rename, LoadList and the
/// watcher reconciles): InstanceList is not thread-safe and the API handlers can interleave.
/// A dedicated type (not a bare SemaphoreSlim) so a handler can inject the lock by what it
/// guards.</summary>
public sealed class InstanceMutationLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Wait() => _gate.Wait();

    public Task WaitAsync() => _gate.WaitAsync();

    public void Release() => _gate.Release();
}
