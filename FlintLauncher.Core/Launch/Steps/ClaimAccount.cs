// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/ClaimAccount.{h,cpp}: claim the account for the duration
// of the launch so a concurrent refresh cannot invalidate it mid-launch.
//
// Deviations from the C++ original:
//  - The C++ reaches into APPLICATION->accounts() by profile name; account selection is the
//    controller's business in the C# port, so the account is injected through the Account
//    property (LaunchController sets it from the session it filled).
//  - The C++ UseLock (launcher/Usable.h) counts uses on MinecraftAccount; the P4 C#
//    MinecraftAccount explicitly did not port the Usable use-counting, so the lock here
//    tracks only its own lifetime (a real counter can hook in at the same spot once the
//    auth layer ports Usable — P5-c follow-up).

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ClaimAccount : LaunchStep
{
    private UseLock? @lock;

    public ClaimAccount(LaunchTask parent, AuthSession? session) : base(parent)
    {
        if (session?.LaunchMode == LaunchMode.Normal)
        {
            AccountForSession = session.PlayerName;
        }
    }

    /// <summary>The session's player name at construction time (C++ lookup key).</summary>
    public string? AccountForSession { get; }

    /// <summary>The account the controller decided on (see header note).</summary>
    public MinecraftAccount? Account { get; set; }

    /// <summary>Minimal port of the Usable/UseLock pair of launcher/Usable.h.</summary>
    public sealed class UseLock : IDisposable
    {
        private readonly MinecraftAccount? _usable;

        public UseLock(MinecraftAccount? usable)
        {
            _usable = usable;
        }

        public void Dispose()
        {
        }
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (Account is not null)
        {
            @lock = new UseLock(Account);
        }
        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override void FinalizeStep() => @lock?.Dispose();

    public override bool CanAbort() => false;
}
