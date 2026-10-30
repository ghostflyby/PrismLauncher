// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
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
// Port of launcher/minecraft/launch/VerifyJavaInstall.{h,cpp}: verify that the selected Java
// feature version is compatible with the instance profile. The probed version/architecture
// come from the state-side probe cache via the instance, not from settings.

using System.Globalization;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class VerifyJavaInstall(LaunchTask parent) : AwaitedStep(parent)
{
    protected override Task RunAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var settings = instance.Settings;
        var detection = instance.JavaDetection();
        var javaArchitecture = detection?.Architecture ?? "";
        var maxMemAlloc = Variant.AsInt(settings.Get("MaxMemAlloc"));

        if (javaArchitecture == "32" && maxMemAlloc > 2048)
        {
            EmitLogLine("Max memory allocation exceeds the supported value.\n" +
                        "The selected installation of Java is 32-bit and doesn't support more than 2048MiB of RAM.\n" +
                        "The instance may not start due to this.", MessageLevel.Error);
        }

        var profile = instance.GetPackProfile().GetProfile()
                      ?? throw new InvalidOperationException("The instance profile is missing.");

        var compatibleMajors = profile.GetCompatibleJavaMajors();

        // the feature version JdkFind read at probe time; null means CheckJava has not probed
        // this java yet or found no release file — never a compatible match
        var languageVersion = detection?.LanguageVersion;

        if (compatibleMajors.Count == 0 || (languageVersion is { } known && compatibleMajors.Contains(known)))
        {
            return Task.CompletedTask;
        }

        if (Variant.AsBool(settings.Get("IgnoreJavaCompatibility")))
        {
            EmitLogLine("Java major version is incompatible. Things might break.\n", MessageLevel.Warning);
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

        throw new InvalidOperationException("Incompatible Java major version");
    }

    public override bool CanAbort() => false;
}
