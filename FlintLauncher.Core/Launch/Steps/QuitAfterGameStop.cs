// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/steps/QuitAfterGameStop.{h,cpp}: quit the launcher after the game
// stops. Upstream calls APPLICATION->quit() and never finishes the step (the process exits
// under it); the headless port raises QuitRequested and then succeeds (documented deviation —
// there is no application event loop to exit here).
namespace FlintLauncher.Core.Launch.Steps;

public sealed class QuitAfterGameStop : LaunchStep
{
    /// <summary>
    /// The C++ APPLICATION->quit() call. The host application installs a handler here that
    /// stops its main loop after the launch task finishes.
    /// </summary>
    public static Action? QuitRequested;

    public QuitAfterGameStop(LaunchTask parent) : base(parent)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        QuitRequested?.Invoke();
        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
