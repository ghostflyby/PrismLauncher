// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
// Port of launcher/minecraft/launch/ScanModFolders.{h,cpp}: scan the mods/coremods/nilmods
// folders before the launch.
//
// Deviation from the C++ original: the C# ResourceFolderModel::update() is synchronous and
// returns whether it ran, so the three updateFinished signals the C++ waits for collapse into
// three synchronous calls (checkDone() always fires right after).

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ScanModFolders(LaunchTask parent) : AwaitedStep(parent)
{
    protected override Task RunAsync(CancellationToken cancellationToken)
    {
        var inst = Parent.Instance;

        // a folder that cannot be enumerated contributes an empty list; the launch runs on
        // whatever was read, so the outcome does not change this step's result
        _ = inst.LoaderModList().Update();
        _ = inst.CoreModList().Update();
        _ = inst.NilModList().Update();
        return Task.CompletedTask;
    }
}
