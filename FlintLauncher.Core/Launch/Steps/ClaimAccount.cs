// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/ClaimAccount.{h,cpp}: claim the account for the duration
// of the launch so a concurrent refresh cannot invalidate it mid-launch.
//
// Deviations from the C++ original:
//  - The C++ reaches into APPLICATION->accounts() by the session's profile name and mounts a
//    UseLock (launcher/Usable.h) on it. Neither half has a ported counterpart: account
//    selection is the controller's business, and the P4 C# MinecraftAccount explicitly did
//    not port the Usable use-counting. With no mechanism to attach, the claim is a no-op —
//    the step exists to keep the launch pipeline's step order and emits success. When the
//    auth layer ports Usable, reintroduce the lock here (git history has the former
//    Account/AccountForSession/UseLock scaffolding).

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ClaimAccount(LaunchTask parent) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
