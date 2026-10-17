// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/VerifyJavaInstall.{h,cpp}: verify that the selected Java
// major version is compatible with the instance profile. The probed version/architecture
// come from the state-side probe cache (S5) via the instance, not from settings (C9).

using FlintLauncher.Core.Java;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class VerifyJavaInstall : LaunchStep
{
    public VerifyJavaInstall(LaunchTask parent) : base(parent)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var packProfile = instance.GetPackProfile();
        var settings = instance.Settings;
        var storedVersion = instance.GetJavaVersion().ToString();
        var ignoreCompatibility = Variant.AsBool(settings.Get("IgnoreJavaCompatibility"));
        var javaArchitecture = instance.JavaDetection()?.Architecture ?? "";
        var maxMemAlloc = Variant.AsInt(settings.Get("MaxMemAlloc"));

        if (javaArchitecture == "32" && maxMemAlloc > 2048)
        {
            EmitLogLine("Max memory allocation exceeds the supported value.\n" +
                        "The selected installation of Java is 32-bit and doesn't support more than 2048MiB of RAM.\n" +
                        "The instance may not start due to this.", MessageLevel.Error);
        }

        var profile = packProfile.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return Task.CompletedTask;
        }
        var compatibleMajors = profile.GetCompatibleJavaMajors();

        var javaVersion = new JavaVersion(storedVersion);

        if (compatibleMajors.Count == 0 || compatibleMajors.Contains(javaVersion.Major))
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        if (ignoreCompatibility)
        {
            EmitLogLine("Java major version is incompatible. Things might break.\n", MessageLevel.Warning);
            EmitSucceeded();
            return Task.CompletedTask;
        }

        EmitLogLine($"This instance is not compatible with Java version {javaVersion.Major}.\n" +
                    "Please switch to one of the following Java versions for this instance:", MessageLevel.Error);
        foreach (var major in compatibleMajors)
        {
            EmitLogLine($"Java version {major}", MessageLevel.Error);
        }
        EmitLogLine("Go to instance Java settings to change your Java version or disable the Java compatibility check if you know what " +
                    "you're doing.", MessageLevel.Error);

        EmitFailed("Incompatible Java major version");
        return Task.CompletedTask;
    }

    public override bool CanAbort() => false;
}
