// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2026 Octol1ttle <l1ttleofficial@outlook.com>
//
// Port of launcher/minecraft/launch/EnsureAvailableMemory.{h,cpp}: warn (or, when the
// LowMemWarning setting asks for it, abort) when the system is low on RAM. The macOS path
// checks the kernel memory-pressure level; the other platforms compare the maximum allocation
// against the available RAM (70% rule). The C++ QMessageBox prompts become the injected
// LaunchUiHooks.ConfirmLaunch hook (headless: launch anyway).

using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class EnsureAvailableMemory(LaunchTask parent, LaunchUiHooks? ui = null) : LaunchStep(parent)
{
    internal override Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = Parent.Instance.Settings;

        if (OperatingSystem.IsMacOS())
        {
            var pressure = HardwareInfo.GetMemoryPressureLevel();
            if (pressure == HardwareInfo.MemoryPressureLevel.Normal)
            {
                return Task.CompletedTask;
            }

            var text = pressure == HardwareInfo.MemoryPressureLevel.Warning
                ? "The system is under increased memory pressure.\n" +
                  "This may lead to lag or slowdowns.\n" +
                  "If possible, close other applications before continuing.\n\n" +
                  "Launch anyway?"
                : "Your system is under critical memory pressure.\n" +
                  "This may lead to severe slowdowns, crashes or system instability.\n" +
                  "It is recommended to close other applications or restart your system.\n\n" +
                  "Launch anyway?";

            const string message = "The system is under high memory pressure";
            if (Variant.AsBool(settings.Get("LowMemWarning")) &&
                ui?.ConfirmLaunch?.Invoke(text) == false)
            {
                EmitLogLine(message, MessageLevel.Fatal);
                throw new InvalidOperationException(message);
            }

            EmitLogLine(message, MessageLevel.Warning);
            return Task.CompletedTask;
        }

        var available = HardwareInfo.AvailableRamMiB();
        if (available == 0)
        {
            return Task.CompletedTask; // could not read
        }

        var settingMin = (ulong)Variant.AsLong(settings.Get("MinMemAlloc"));
        var settingMax = (ulong)Variant.AsLong(settings.Get("MaxMemAlloc"));
        var max = Math.Max(settingMin, settingMax);

        if (max * 0.7 > available)
        {
            const string message = "Not enough RAM available to launch this instance";
            if (Variant.AsBool(settings.Get("LowMemWarning")))
            {
                var dialogText =
                    "There might not be enough free RAM to launch this instance with the current memory settings.\n\n" +
                    $"Maximum allocated: {max} MiB\nFree: {available} MiB (out of {HardwareInfo.TotalRamMiB()} MiB total)\n\n" +
                    "Launch anyway? This may cause slowdowns in the game and your system.";
                if (ui?.ConfirmLaunch?.Invoke(dialogText) == false)
                {
                    EmitLogLine(message, MessageLevel.Fatal);
                    throw new InvalidOperationException(message);
                }
            }

            EmitLogLine(message, MessageLevel.Warning);
        }

        return Task.CompletedTask;
    }
}
