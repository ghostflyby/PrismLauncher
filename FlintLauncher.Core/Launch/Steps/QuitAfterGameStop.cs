// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 dada513 <dada513@protonmail.com>
//
// Port of launcher/launch/steps/QuitAfterGameStop.{h,cpp}: quit the launcher after the game
// stops. Upstream calls APPLICATION->quit() and never finishes the step (the process exits
// under it); the headless port raises Services.Ui.QuitRequested and then succeeds (documented
// deviation — there is no application event loop to exit here).

namespace FlintLauncher.Core.Launch.Steps;

public sealed class QuitAfterGameStop(LaunchTask parent) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Services.Ui.QuitRequested?.Invoke();
        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
