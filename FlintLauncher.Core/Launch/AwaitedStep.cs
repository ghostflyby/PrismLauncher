// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.Core.Launch;

/// <summary>A launch step whose end is the end of its body: returning succeeds it and throwing
/// fails it, with the exception's message as the reason.
/// <para>A step whose end is an external event (the game exiting, a process reporting its exit
/// code) parks its body on that event: the body still returns to succeed, but only once the
/// event has arrived.</para></summary>
public abstract class AwaitedStep(LaunchTask parent) : LaunchStep(parent)
{
    internal sealed override Task ExecuteAsync(CancellationToken cancellationToken) =>
        RunAsync(cancellationToken);

    /// <summary>The step's work. Returning succeeds the step; throwing fails it.</summary>
    protected abstract Task RunAsync(CancellationToken cancellationToken);
}
