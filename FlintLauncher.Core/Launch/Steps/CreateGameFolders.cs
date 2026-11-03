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
// Creates the instance's game folder and the server-resource-packs folder (a workaround for
// Minecraft bug MCL-3732).

using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class CreateGameFolders(LaunchTask parent) : LaunchStep(parent)
{
    internal override Task RunAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;

        if (!FileOps.EnsureFolderPathExists(new AbsolutePath(instance.GameRoot)))
        {
            var reason = "Couldn't create the main game folder";
            EmitLogLine(reason, MessageLevel.Error);
            throw new InvalidOperationException(reason);
        }

        // a failure here is a warning only: the game runs without the folder
        if (!FileOps.EnsureFolderPathExists(new AbsolutePath(instance.GameRoot) / "server-resource-packs"))
        {
            EmitLogLine("Couldn't create the 'server-resource-packs' folder", MessageLevel.Error);
        }

        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
