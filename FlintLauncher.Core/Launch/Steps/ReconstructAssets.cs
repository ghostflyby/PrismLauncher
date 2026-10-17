// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/ReconstructAssets.{h,cpp}: rebuild the virtual assets
// folder for the profile's asset index (see Launch/AssetsUtils.cs).

using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ReconstructAssets : LaunchStep
{
    public ReconstructAssets(LaunchTask parent) : base(parent) { }

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

        var assetsRoot = instance.Paths?.AssetsRoot
                         ?? FileSystem.PathCombine("assets", "");
        if (!AssetsUtils.ReconstructAssets(assets.Id, instance.ResourcesDir(), assetsRoot))
        {
            EmitLogLine("Failed to reconstruct Minecraft assets.", MessageLevel.Error);
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }
}
