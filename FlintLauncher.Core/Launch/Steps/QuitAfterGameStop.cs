// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/steps/QuitAfterGameStop.{h,cpp}: quit the launcher after the game
// stops. Upstream calls APPLICATION->quit() and never finishes the step (the process exits
// under it); the headless port raises Services.Ui.QuitRequested and then succeeds (documented
// deviation — there is no application event loop to exit here).

namespace FlintLauncher.Core.Launch.Steps;

public sealed class QuitAfterGameStop : LaunchStep
{
    public QuitAfterGameStop(LaunchTask parent) : base(parent) { }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Services.Ui.QuitRequested?.Invoke();
        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
