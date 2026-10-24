// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2026 Octol1ttle <l1ttleofficial@outlook.com>
//
// Port of launcher/minecraft/launch/EnsureOfflineLibraries.{h,cpp}: fail the launch with a
// clear message when the instance's libraries are missing (offline mode has no downloads).

using FlintLauncher.Core.Instances;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class EnsureOfflineLibraries(LaunchTask parent, MinecraftInstance instance) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var profile = instance.GetPackProfile().GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return Task.CompletedTask;
        }

        var allJars = new List<AbsolutePath>();
        profile.GetLibraryFiles(instance.RuntimeContext, allJars, allJars, instance.GetLocalLibraryPath(),
            instance.BinRoot(), false, storageBase: instance.LibraryStorageBase());

        var missing = new List<AbsolutePath>();
        foreach (var jar in allJars)
        {
            if (!jar.ExistsFile())
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
