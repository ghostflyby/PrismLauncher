// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/CreateGameFolders.{h,cpp}: create the main .minecraft
// folder for the instance and any other necessary folders.

using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class CreateGameFolders(LaunchTask parent) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;

        if (!FileSystem.EnsureFolderPathExists(instance.GameRoot()))
        {
            EmitLogLine("Couldn't create the main game folder", MessageLevel.Error);
            EmitFailed("Couldn't create the main game folder");
            return Task.CompletedTask;
        }

        // HACK: this is a workaround for MCL-3732 - 'server-resource-packs' folder is created.
        if (!FileSystem.EnsureFolderPathExists(FileSystem.PathCombine(instance.GameRoot(), "server-resource-packs")))
        {
            EmitLogLine("Couldn't create the 'server-resource-packs' folder", MessageLevel.Error);
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
