// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/update/FoldersTask.{h,cpp}: make sure the instance's game folder
// exists before the update pipeline runs.
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class FoldersTask : FlintTask
{
    private readonly MinecraftInstance _inst;

    public FoldersTask(MinecraftInstance inst)
    {
        _inst = inst;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Make directories
        var mcDir = _inst.GameRoot();
        if (!Directory.Exists(mcDir))
        {
            try
            {
                Directory.CreateDirectory(mcDir);
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
