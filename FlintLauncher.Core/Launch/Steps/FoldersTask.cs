// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
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
// Port of launcher/minecraft/update/FoldersTask.{h,cpp}: make sure the instance's game folder
// exists before the update pipeline runs.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Tasks;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class FoldersTask(MinecraftInstance inst) : FlintTask
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Make directories
        var mcDir = inst.GameRoot;
        if (!mcDir.ExistsDirectory())
        {
            try
            {
                Directory.CreateDirectory(mcDir.Value);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                EmitFailed("Failed to create folder for Minecraft binaries.");
                return Task.CompletedTask;
            }
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }
}
