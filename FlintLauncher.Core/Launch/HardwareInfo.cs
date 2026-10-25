// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/HardwareInfo.{h,cpp} (the parts the launch pipeline consumes) and the
// MacOSHardwareInfo memory-pressure surface.
//
// Port notes:
//  - macOS: sysctlbyname maps to the libc sysctlbyname P/Invoke (machdep.cpu.brand_string,
//    hw.memsize, kern.memorystatus_vm_pressure_level); gpuInfo shells out to
//    `system_profiler SPDisplaysDataType` exactly like the C++ popen.
//  - Linux: /proc/cpuinfo and /proc/meminfo are parsed like the C++.
//  - Windows: CPU name + memory come from GlobalMemoryStatusEx/registry through P/Invoke.
//    The C++ GPU enumeration uses DXGI COM interfaces which are not portable to AOT; the
//    port returns the C++ "GPU discovery failed" message with a documented reason instead.
//  - availableRamMiB() on macOS returns 0 in the C++ as well (EnsureAvailableMemory then
//    skips the check).

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace FlintLauncher.Core.Launch;

public static partial class HardwareInfo
{
    public static string CpuInfo()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (SysCtlByName("machdep.cpu.brand_string", out var value))
            {
                return value;
            }

            return "";
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    // model name      : AMD Ryzen 7 5800X 8-Core Processor
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        return AfterColon(line);
                    }
                }
            }
            catch (IOException) { }

            return "unknown";
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue("ProcessorNameString") as string ?? "";
            }
            catch (Exception e) when (e is SecurityException or IOException)
            {
                return "";
            }
        }

        return "";
    }

    public static ulong TotalRamMiB()
    {
        if (OperatingSystem.IsMacOS())
        {
            // hw.memsize is a raw int64 (the C++ getSystemRam reads it the same way), not a
            // string — the string SysCtlByName overload would decode binary bytes and fail
            if (SysCtlByName("hw.memsize", out ulong memsize))
            {
                // transforming bytes -> mib
                return memsize / 1024 / 1024;
            }

            return 0;
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadMemInfo("MemTotal");
        }

        if (OperatingSystem.IsWindows())
        {
            if (TryGetMemoryStatus(out var status))
            {
                return status.ullTotalPhys / 1024 / 1024;
            }
        }

        return 0;
    }

    public static ulong AvailableRamMiB()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0;
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadMemInfo("MemAvailable");
        }

        if (OperatingSystem.IsWindows())
        {
            if (TryGetMemoryStatus(out var status))
            {
                return status.ullAvailPhys / 1024 / 1024;
            }
        }

        return 0;
    }

    public static List<string> GpuInfo()
    {
        var outList = new List<string>();
        if (OperatingSystem.IsMacOS())
        {
            var success = ReadFromOutput("system_profiler SPDisplaysDataType", str =>
            {
                // Chipset Model: Intel HD Graphics 620
                if (str.Contains("Chipset Model"))
                {
                    outList.Add("GPU: " + AfterColon(str));
                }
            });
            if (!success)
            {
                return ["GPU discovery failed: could not read from system_profiler"];
            }

            return outList;
        }

        if (OperatingSystem.IsLinux())
        {
            var success = ReadFromOutput("lspci", str =>
            {
                if (str.Contains("VGA compatible controller") || str.Contains("3D controller") ||
                    str.Contains("Display controller"))
                {
                    outList.Add("GPU: " + AfterColon(str));
                }
            });
            if (!success)
            {
                return outList; // the C++ returns an empty list when lspci produced nothing usable
            }

            return outList;
        }

        // Windows: the C++ enumerates DXGI adapters; COM interop is not AOT-safe, so the
        // discovery failure is reported the same way the C++ reports a failed DXGI factory.
        return ["GPU discovery failed: could not create DXGI factory"];
    }

    // ------------------------------------------------------------- MacOSHardwareInfo port

    /// <summary>Port of MacOSHardwareInfo::MemoryPressureLevel.</summary>
    [SupportedOSPlatform("macos")]
    public enum MemoryPressureLevel : uint
    {
        Normal = 1,
        Warning = 2,
        Critical = 4,
    }

    [SupportedOSPlatform("macos")]
    public static MemoryPressureLevel GetMemoryPressureLevel()
    {
        if (SysCtlByName("kern.memorystatus_vm_pressure_level", out uint level) &&
            level is 1 or 2 or 4)
        {
            return (MemoryPressureLevel)level;
        }

        return MemoryPressureLevel.Normal;
    }

    /// <summary>Port of MacOSHardwareInfo::memoryPressureLevelName.</summary>
    [SupportedOSPlatform("macos")]
    public static string MemoryPressureLevelName() => GetMemoryPressureLevel() switch
    {
        // The names are internal, users refer to levels by their graph colors in Activity Monitor
        MemoryPressureLevel.Normal => "Green",
        MemoryPressureLevel.Warning => "Yellow",
        MemoryPressureLevel.Critical => "Red",
        _ => "",
    };

    // ------------------------------------------------------------------------ internals

    private static string AfterColon(string str)
    {
        var index = str.IndexOf(':');
        return index < 0 ? str.Trim() : str[(index + 2)..].Trim();
    }

    [SupportedOSPlatform("linux")]
    private static ulong ReadMemInfo(string searchTarget)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                // MemTotal:       16287480 kB
                if (!line.StartsWith(searchTarget, StringComparison.Ordinal))
                {
                    continue;
                }

                var simplified = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var parts = simplified.Split(' ');
                if (parts.Length < 2 || !ulong.TryParse(parts[1], out var total))
                {
                    return 0;
                }

                // transforming kib -> mib
                return total / 1024;
            }
        }
        catch (IOException) { }

        return 0;
    }

    private static bool ReadFromOutput(string command, Action<string> function)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", command },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process.Start();
            string output;
            try
            {
                output = process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)).Result;
            }
            catch (AggregateException)
            {
                process.Kill();
                return false;
            }

            var exitCode = process.WaitForExit(10000) ? process.ExitCode : -1;
            if (exitCode != 0)
            {
                return false;
            }

            foreach (var line in output.Split('\n'))
            {
                function(line);
            }

            return true;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("macos")]
    private static bool SysCtlByName(string name, out string value)
    {
        value = "";

        var buffer = new byte[512];
        var size = (nuint)buffer.Length;
        var result = SysctlByName(name, buffer, ref size, 0, 0);
        if (result != 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(size, (nuint)buffer.Length)).TrimEnd('\0');
        return true;
    }

    [SupportedOSPlatform("macos")]
    private static unsafe bool SysCtlByName<T>(string name, out T value) where T : unmanaged
    {
        Span<T> t = stackalloc T[1];
        var size = (nuint)sizeof(T);
        if (SysctlByName(name, MemoryMarshal.AsBytes(t), ref size, 0, 0) == 0)
        {
            if (size != (nuint)sizeof(T))
            {
                value = default;
                return false;
            }
            value = t[0];
            return true;
        }

        value = default;
        return false;
    }

    // oldSize is a size_t* on the C side (8 bytes on LP64) — a ref uint would let the kernel
    // read/write 4 bytes past the managed slot

    [LibraryImport("libc", EntryPoint = "sysctlbyname", SetLastError = false, StringMarshalling = StringMarshalling.Utf8)]
    [SupportedOSPlatform("macos")]
    private static partial int SysctlByName(string name, Span<byte> oldValue, ref nuint oldSize, nint newValue, nuint newValueSize);

    // Field names mirror MEMORYSTATUSEX so the layout can be checked against the Windows
    // SDK header; renaming them to the project convention would break that correspondence.
#pragma warning disable IDE1006
    [StructLayout(LayoutKind.Sequential)]
    [SupportedOSPlatform("windows")]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
#pragma warning restore IDE1006

    // ref, not out: the LibraryImport stub zeroes an out parameter before the call, which
    // would wipe the caller-set dwLength GlobalMemoryStatusEx requires on entry
    [LibraryImport("kernel32", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [SupportedOSPlatform("windows")]
    private static bool TryGetMemoryStatus(out MemoryStatusEx status)
    {
        status = default;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref status);
    }
}
