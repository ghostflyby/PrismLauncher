// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
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
// Port of launcher/minecraft/launch/ReconstructAssets.{h,cpp}: rebuild the virtual assets
// folder for the profile's asset index (see Launch/AssetsUtils.cs).

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ReconstructAssets(LaunchTask parent) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var components = instance.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return Task.CompletedTask;
        }

        var assets = profile.GetMinecraftAssets();

        var assetsRoot = instance.AssetsRoot;
        if (!AssetsUtils.ReconstructAssets(assets.Id, instance.ResourcesDir(), assetsRoot))
        {
            EmitLogLine("Failed to reconstruct Minecraft assets.", MessageLevel.Error);
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }
}
