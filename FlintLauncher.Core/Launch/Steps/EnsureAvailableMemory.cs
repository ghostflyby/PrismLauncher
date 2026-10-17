// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/EnsureAvailableMemory.{h,cpp}: warn (or, when the
// LowMemWarning setting asks for it, abort) when the system is low on RAM. The macOS path
// checks the kernel memory-pressure level; the other platforms compare the maximum allocation
// against the available RAM (70% rule). The C++ QMessageBox prompts become the
// Services.Ui.ConfirmLaunch hook (headless: launch anyway).
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class EnsureAvailableMemory : LaunchStep
{
    private readonly MinecraftInstance _instance;

    public EnsureAvailableMemory(LaunchTask parent, MinecraftInstance instance) : base(parent)
    {
        _instance = instance;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            string text;
            switch (HardwareInfo.GetMemoryPressureLevel())
            {
                case HardwareInfo.MemoryPressureLevel.Normal:
                    EmitSucceeded();
                    return Task.CompletedTask;
                case HardwareInfo.MemoryPressureLevel.Warning:
                    text = "The system is under increased memory pressure.\n" +
                           "This may lead to lag or slowdowns.\n" +
                           "If possible, close other applications before continuing.\n\n" +
                           "Launch anyway?";
                    break;
                case HardwareInfo.MemoryPressureLevel.Critical:
                default:
                    text = "Your system is under critical memory pressure.\n" +
                           "This may lead to severe slowdowns, crashes or system instability.\n" +
                           "It is recommended to close other applications or restart your system.\n\n" +
                           "Launch anyway?";
                    break;
            }

            var shouldAbort = false;
            if (Variant.AsBool(_instance.Settings().Get("LowMemWarning")))
            {
                shouldAbort = Services.Ui.ConfirmLaunch?.Invoke(text) == false;
            }

            const string message = "The system is under high memory pressure";
            if (shouldAbort)
            {
                EmitLogLine(message, MessageLevel.Fatal);
                EmitFailed(message);
                return Task.CompletedTask;
            }

            EmitLogLine(message, MessageLevel.Warning);
            EmitSucceeded();
            return Task.CompletedTask;
        }

        var available = HardwareInfo.AvailableRamMiB();
        if (available == 0)
        {
            // could not read
            EmitSucceeded();
            return Task.CompletedTask;
        }

        var settingMin = (ulong)Variant.AsLong(_instance.Settings().Get("MinMemAlloc"));
        var settingMax = (ulong)Variant.AsLong(_instance.Settings().Get("MaxMemAlloc"));
        var max = Math.Max(settingMin, settingMax);

        if (max * 0.7 > available)
        {
            var shouldAbort = false;

            if (Variant.AsBool(_instance.Settings().Get("LowMemWarning")))
            {
                var dialogText =
                    "There might not be enough free RAM to launch this instance with the current memory settings.\n\n" +
                    $"Maximum allocated: {max} MiB\nFree: {available} MiB (out of {HardwareInfo.TotalRamMiB()} MiB total)\n\n" +
                    "Launch anyway? This may cause slowdowns in the game and your system.";
                shouldAbort = Services.Ui.ConfirmLaunch?.Invoke(dialogText) == false;
            }

            const string message = "Not enough RAM available to launch this instance";
            if (shouldAbort)
            {
                EmitLogLine(message, MessageLevel.Fatal);
                EmitFailed(message);
                return Task.CompletedTask;
            }

            EmitLogLine(message, MessageLevel.Warning);
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }
}
