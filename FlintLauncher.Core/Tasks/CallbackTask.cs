// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.Core.Tasks;

/// <summary>A task step that runs an async delegate. The delegate's exceptions route to the
/// terminal states like any ExecuteAsync failure (StartAsync never throws); a delegate that
/// completes normally succeeds the step.</summary>
public sealed class CallbackTask(Func<CancellationToken, Task> body) : FlintTask
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await body(cancellationToken).ConfigureAwait(false);
        EmitSucceeded();
    }
}
