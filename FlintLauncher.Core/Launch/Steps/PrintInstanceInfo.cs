// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/PrintInstanceInfo.{h,cpp}: print system + instance info
// into the launch log. The FreeBSD sysctl/pciconf branches are not ported (Q_OS_FREEBSD only).

using System.Runtime.InteropServices;
using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class PrintInstanceInfo(LaunchTask parent, AuthSession? session, MinecraftTarget? targetToJoin) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var log = new List<string>
        {
            "",
            "OS: " + $"{OsProductName()} | {KernelType()} | {KernelVersion()}",
        };

        log.Add("CPU: " + HardwareInfo.CpuInfo());
        if (OperatingSystem.IsMacOS())
        {
            log.Add("Memory pressure level: " + HardwareInfo.MemoryPressureLevelName());
        }
        else
        {
            log.Add($"RAM: {HardwareInfo.TotalRamMiB()} MiB (available: {HardwareInfo.AvailableRamMiB()} MiB)");
        }

        log.AddRange(HardwareInfo.GpuInfo());
        log.Add("");

        EmitLogLines(log, MessageLevel.Launcher);
        EmitLogLines(instance.VerboseDescription(session, targetToJoin), MessageLevel.Launcher);
        EmitSucceeded();
        return Task.CompletedTask;
    }

    /// <summary>Port of QSysInfo::prettyProductName (reduced, no marketing-version lookup).</summary>
    private static string OsProductName()
    {
        var os = Environment.OSVersion;
        if (OperatingSystem.IsMacOS())
        {
            return $"macOS {os.Version.ToString(2)}";
        }

        if (OperatingSystem.IsWindows())
        {
            return $"Windows {os.Version}";
        }

        return RuntimeInformation.OSDescription.Trim();
    }

    /// <summary>Port of QSysInfo::kernelType.</summary>
    private static string KernelType()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "darwin";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsWindows())
        {
            return "nt";
        }

        return "unknown";
    }

    /// <summary>Port of QSysInfo::kernelVersion.</summary>
    private static string KernelVersion() => Environment.OSVersion.VersionString;
}
