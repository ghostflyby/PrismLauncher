// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2022 r58Playz <r58playz@gmail.com>
// SPDX-FileCopyrightText: 2024 timoreo <contact@timoreo.fr>
// SPDX-FileCopyrightText: 2024 Trial97 <alexandru.tripon97@gmail.com>
// SPDX-FileCopyrightText: 2025 TheKodeToad <TheKodeToad@proton.me>
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
