// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/SysInfo.{h,cpp} (the three functions the launch pipeline uses):
// currentSystem(), useQTForArch() and the getSupportedJavaArchitecture() free function.

using System.Runtime.InteropServices;

namespace FlintLauncher.Core.Launch;

public static class SysInfo
{
    /// <summary>Port of SysInfo::currentSystem.</summary>
    public static string CurrentSystem()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "osx";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return "freebsd";
        }

        return "unknown";
    }

    /// <summary>Port of SysInfo::useQTForArch (QSysInfo::buildCpuArchitecture).</summary>
    public static string UseQtForArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "i386",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "unknown",
    };

    /// <summary>Port of the getSupportedJavaArchitecture free function.</summary>
    public static string GetSupportedJavaArchitecture()
    {
        var sys = CurrentSystem();
        var arch = UseQtForArch();
        if (sys == "windows")
        {
            if (arch == "x86_64")
            {
                return "windows-x64";
            }

            if (arch == "i386")
            {
                return "windows-x86";
            }

            // Unknown, maybe arm, appending arch
            return "windows-" + arch;
        }

        if (sys == "osx")
        {
            if (arch == "arm64")
            {
                return "mac-os-arm64";
            }

            if (arch.Contains("64"))
            {
                return "mac-os-x64";
            }

            if (arch.Contains("86"))
            {
                return "mac-os-x86";
            }

            // Unknown, maybe something new, appending arch
            return "mac-os-" + arch;
        }

        if (sys == "linux")
        {
            if (arch == "x86_64")
            {
                return "linux-x64";
            }

            if (arch == "i386")
            {
                return "linux-x86";
            }

            // will work for arm32 arm(64)
            return "linux-" + arch;
        }

        return "";
    }
}
