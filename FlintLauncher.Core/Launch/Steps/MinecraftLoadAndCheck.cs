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
// Port of launcher/minecraft/MinecraftLoadAndCheck.{h,cpp}: load the instance's component
// metadata (resolving it online or offline) before the launch continues.
//
// Deviations from the C++ original:
//  - PackProfile::reload() starts the ComponentUpdateTask and returns; the C++ task then
//    connects to the running task's signals. ReloadAsync() here awaits the resolve, so the
//    resolution's failure is this step's failure instead of a propagated signal.
//  - updateLatestMinecraft() (the "UseLatestMinecraftVersion" branch) was deferred and
//    is not ported, so the whole branch is skipped (documented follow-up).

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Launch.Steps;

public static class MinecraftLoadAndCheck
{
    /// <summary>Resolves the instance's component metadata. Returning is success; any other
    /// exception is a failure whose message is the reason.</summary>
    public static async Task RunAsync(MinecraftInstance inst, NetMode netmode, CancellationToken cancellationToken)
    {
        // add offline metadata load task
        // (the UseLatestMinecraftVersion/updateLatestMinecraft branch is not ported — see header)
        var components = inst.GetPackProfile();
        var result = await components.ReloadAsync(netmode, cancellationToken).ConfigureAwait(false);
        if (!result)
        {
            throw new InvalidOperationException(result.ErrorMessage);
        }
    }
}
