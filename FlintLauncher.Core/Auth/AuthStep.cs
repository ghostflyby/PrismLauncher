// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/AuthStep.h (including the AccountTaskState enum).
//
// The Qt signal finished(AccountTaskState, QString) becomes the Finished event and the
// perform()/abort() slots become PerformAsync()/Abort(); a step reports its outcome through
// EmitFinished exactly once (a Working result keeps the chain going, anything else is terminal).

namespace FlintLauncher.Core.Auth;

/// <summary>
/// Enum for describing the state of the current task.
/// Used by the getStateMessage function to determine what the status message should be.
/// </summary>
public enum AccountTaskState
{
    Created,
    Working,
    Succeeded,

    /// <summary>MSA Client ID has changed. Tell user to relogin.</summary>
    Disabled,

    /// <summary>soft failure. authentication went through partially.</summary>
    FailedSoft,

    /// <summary>hard failure. main tokens are invalid.</summary>
    FailedHard,

    /// <summary>hard failure. main tokens are invalid, and the account no longer exists.</summary>
    FailedGone,

    /// <summary>soft failure. authentication failed in the first step in a 'soft' way.</summary>
    Offline,
}

public abstract class AuthStep(AccountData data)
{
    protected AccountData Data { get; } = data;

    public abstract string Describe();

    /// <summary>Port of the perform() slot; reports through <see cref="EmitFinished"/>.</summary>
    public abstract Task PerformAsync(CancellationToken cancellationToken);

    /// <summary>Port of the abort() slot.</summary>
    public virtual void Abort() { }

    /// <summary>Port of the finished(AccountTaskState, QString) signal.</summary>
    public event Action<AccountTaskState, string>? Finished;

    protected void EmitFinished(AccountTaskState resultingState, string message)
    {
        Finished?.Invoke(resultingState, message);
    }
}
