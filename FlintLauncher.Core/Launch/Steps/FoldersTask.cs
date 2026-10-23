// SPDX-License-Identifier: GPL-3.0-only
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
