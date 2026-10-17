// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/EnsureOfflineLibraries.{h,cpp}: fail the launch with a
// clear message when the instance's libraries are missing (offline mode has no downloads).

using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class EnsureOfflineLibraries : LaunchStep
{
    private readonly MinecraftInstance _instance;

    public EnsureOfflineLibraries(LaunchTask parent, MinecraftInstance instance) : base(parent)
    {
        _instance = instance;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var profile = _instance.GetPackProfile().GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return Task.CompletedTask;
        }
        var allJars = new List<string>();
        profile.GetLibraryFiles(_instance.RuntimeContext, allJars, allJars, _instance.GetLocalLibraryPath(),
            _instance.BinRoot(), false, storageBase: _instance.LibraryStorageBase());

        var missing = new List<string>();
        foreach (var jar in allJars)
        {
            if (!File.Exists(jar))
            {
                missing.Add(jar);
            }
        }

        if (missing.Count == 0)
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        EmitLogLine("Missing libraries:", MessageLevel.Error);
        foreach (var jar in missing)
        {
            EmitLogLine("  " + jar, MessageLevel.Error);
        }
        EmitLogLine("\nThis instance cannot be launched because some libraries are missing or have not been downloaded yet. Please " +
                    "try again in online mode with a working Internet connection", MessageLevel.Fatal);
        EmitFailed("Required libraries are missing");
        return Task.CompletedTask;
    }
}
