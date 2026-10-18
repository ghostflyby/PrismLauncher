// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/RuntimeContext.h plus the currentSystem() part of launcher/SysInfo.{h,cpp}
// (the only SysInfo function the version-metadata layer needs). The SettingsObject hookup
// (updateFromInstanceSettings) arrives with the instance layer and is not ported yet.

namespace FlintLauncher.Core.Minecraft;

/// <summary>Port of the currentSystem() part of the SysInfo namespace.</summary>
public static class SysInfo
{
    public static string CurrentSystem()
    {
        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "osx";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return "freebsd";
        }
#pragma warning disable CA1418 // "OpenBSD" is not a platform name the analyzer knows
        if (OperatingSystem.IsOSPlatform("OpenBSD"))
#pragma warning restore CA1418
        {
            return "openbsd";
        }

        return "unknown";
    }
}

public struct RuntimeContext()
{
    public string JavaArchitecture = "";

    public string JavaRealArchitecture = "";

    public string System = SysInfo.CurrentSystem();

    public readonly string MappedJavaRealArchitecture()
    {
        switch (JavaRealArchitecture)
        {
            case "amd64":
                return "x86_64";
            case "i386":
            case "i686":
                return "x86";
            case "aarch64":
                return "arm64";
            case "arm":
            case "armhf":
                return "arm32";
            default:
                return JavaRealArchitecture;
        }
    }

    public readonly string GetClassifier() => System + "-" + MappedJavaRealArchitecture();

    // "Legacy" refers to the fact that Mojang assumed that these are the only two architectures
    public readonly bool IsLegacyArch()
    {
        var mapped = MappedJavaRealArchitecture();
        return mapped is "x86_64" or "x86";
    }

    public readonly bool ClassifierMatches(string target)
    {
        // try to match precise classifier "[os]-[arch]"
        var x = target == GetClassifier();
        // try to match imprecise classifier on legacy architectures "[os]"
        if (!x && IsLegacyArch())
        {
            x = target == System;
        }

        return x;
    }
}
