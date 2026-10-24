// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of launcher/minecraft/launch/ClaimAccount.{h,cpp}: claim the account for the duration
// of the launch so a concurrent refresh cannot invalidate it mid-launch.
//
// Deviations from the C++ original:
//  - The C++ reaches into APPLICATION->accounts() by the session's profile name and mounts a
//    UseLock (launcher/Usable.h) on it. Neither half has a ported counterpart: account
//    selection is the controller's business, and the C# MinecraftAccount explicitly did
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
