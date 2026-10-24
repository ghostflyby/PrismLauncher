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
// Port of launcher/minecraft/launch/CreateGameFolders.{h,cpp}: create the main .minecraft
// folder for the instance and any other necessary folders.

using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class CreateGameFolders(LaunchTask parent) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;

        if (!FileOps.EnsureFolderPathExists(new AbsolutePath(instance.GameRoot)))
        {
            EmitLogLine("Couldn't create the main game folder", MessageLevel.Error);
            EmitFailed("Couldn't create the main game folder");
            return Task.CompletedTask;
        }

        // HACK: this is a workaround for MCL-3732 - 'server-resource-packs' folder is created.
        if (!FileOps.EnsureFolderPathExists((new AbsolutePath(instance.GameRoot) / "server-resource-packs")))
        {
            EmitLogLine("Couldn't create the 'server-resource-packs' folder", MessageLevel.Error);
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
