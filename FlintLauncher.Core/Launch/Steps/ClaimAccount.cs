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
// Claims the account for the duration of the launch so a concurrent refresh cannot invalidate
// it mid-launch.
//
// Nothing is claimed yet: account selection is the controller's business and MinecraftAccount
// has no use-counting to lock. The step is a no-op that keeps the pipeline's stage order.

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ClaimAccount(LaunchTask parent) : LaunchStep(parent)
{
    internal override Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override bool CanAbort() => false;
}
