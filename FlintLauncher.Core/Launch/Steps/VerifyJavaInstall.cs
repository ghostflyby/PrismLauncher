// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/VerifyJavaInstall.{h,cpp}: verify that the selected Java
// feature version is compatible with the instance profile. The probed version/architecture
// come from the state-side probe cache via the instance, not from settings.

using System.Globalization;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class VerifyJavaInstall(LaunchTask parent) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var packProfile = instance.GetPackProfile();
        var settings = instance.Settings;
        var detection = instance.JavaDetection();
        var ignoreCompatibility = Variant.AsBool(settings.Get("IgnoreJavaCompatibility"));
        var javaArchitecture = detection?.Architecture ?? "";
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

        // the feature version JdkFind read at probe time; null means CheckJava has not
        // probed this java yet or found no release file — never a compatible match
        var languageVersion = detection?.LanguageVersion;

        if (compatibleMajors.Count == 0 || (languageVersion is { } known && compatibleMajors.Contains(known)))
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

        var javaDescription = languageVersion?.ToString(CultureInfo.InvariantCulture) ?? detection?.Version ?? "unknown";
        EmitLogLine($"This instance is not compatible with Java version {javaDescription}.\n" +
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
