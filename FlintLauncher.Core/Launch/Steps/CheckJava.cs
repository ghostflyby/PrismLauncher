// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/steps/CheckJava.{h,cpp}: verify the configured java binary and,
// when its signature changed, probe it through JdkFind.FromExecutable (the JavaCheck.jar
// checker died in the migration) and store the probed properties in the launcher runtime
// state's path-keyed probe cache (launcher-state.json, S5 — the C++ stored them as settings
// keys, moved out of the config world by CONFIG C9).

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;
using JdkFind;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class CheckJava : LaunchStep
{
    private string javaPath = "";
    private string javaSignature = "";
    private string detectKey = "";

    public CheckJava(LaunchTask parent) : base(parent) { }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var settings = instance.Settings;

        var javaPathSetting = Variant.AsString(settings.Get("JavaPath"));
        javaPath = LauncherJars.ResolveExecutable(javaPathSetting);

        // C7: the override gate is derived — "a java location member is set at the instance
        // layer" (the legacy "OverrideJava" spelling was never a registered key and stays a
        // harmless unknown read, like upstream's leftover)
        var perInstance = Variant.AsBool(settings.Get("OverrideJava")) || Variant.AsBool(settings.Get("OverrideJavaLocation"));

        var realJavaPath = LauncherJars.FindExecutable(javaPath);
        if (realJavaPath.Length == 0)
        {
            if (perInstance)
            {
                EmitLogLine($"The Java binary \"{javaPathSetting}\" couldn't be found. Please fix the Java path " +
                            "override in the instance's settings or disable it.", MessageLevel.Warning);
            }
            else
            {
                EmitLogLine($"The Java binary \"{javaPathSetting}\" couldn't be found. Please set up Java in " +
                            "the settings.", MessageLevel.Warning);
            }

            EmitFailed("Java path is not valid.");
            return;
        }

        EmitLogLine("Java path is:\n  " + javaPath, MessageLevel.Launcher);

        var javaInfo = new FileInfo(realJavaPath);
        var javaUnixTime = new DateTimeOffset(javaInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();

        javaSignature = ComputeJavaSignature(javaUnixTime, javaPath);

        // S5: the probe cache is launcher-state.json's path-keyed map (keyed through
        // MinecraftInstance.JavaDetectKey — the same resolution the signature covers),
        // replacing the five global settings keys that overwrote each other across JVMs
        // (CONFIG C9)
        var detectKey = MinecraftInstance.JavaDetectKey(javaPathSetting);
        this.detectKey = detectKey;
        var stored = Services.LauncherState?.Current.Java.Detections.GetValueOrDefault(detectKey);

        // if timestamps are not the same, or something is missing, check!
        if (stored is null || stored.Signature != javaSignature || stored.Version.Length == 0 ||
            stored.Architecture.Length == 0 || stored.RealArchitecture.Length == 0 || stored.Vendor.Length == 0)
        {
            await ProbeJavaAsync(realJavaPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        PrintJavaInfo(stored.Version, stored.Architecture, stored.RealArchitecture, stored.Vendor);
        Parent.Instance.UpdateRuntimeContext();
        EmitSucceeded();
    }

    /// <summary>Probes the selected binary through JdkFind.FromExecutable: path-keyed, the
    /// child running with JVM-affecting environment variables removed, the verdict on
    /// <see cref="JavaExecutable.StartFailure"/> (spawn error, timeout, non-zero exit —
    /// the old JavaCheck.jar checker's Errored), and the probed properties feeding the
    /// launcher-state quintet. A null return (no runnable JVM and no release file) maps to
    /// the same refusal; a binary that ran but reported an incomplete trio maps to the old
    /// ReturnedInvalidData semantics (warn and proceed, nothing cached).</summary>
    private async Task ProbeJavaAsync(string realJavaPath, CancellationToken cancellationToken)
    {
        EmitLogLine("Checking Java version...", MessageLevel.Launcher);
        var executable = await JdkFinder.Default.FromExecutableAsync(realJavaPath, cancellationToken).ConfigureAwait(false);
        if (executable is null || executable.StartFailure is not null)
        {
            // Error message displayed if java can't start
            EmitLogLine("Could not start java:", MessageLevel.Error);
            if (executable?.StartFailure is { } failure)
            {
                EmitLogLines(failure.Split('\n'), MessageLevel.Error);
            }

            EmitLogLine("\nCheck your Java settings.", MessageLevel.Launcher);
            EmitFailed("Could not start java!");
            return;
        }

        var osArch = executable.Architecture;
        var version = executable.Version.Original;
        var vendor = executable.VendorRaw;
        if (osArch.Length == 0 || version.Length == 0 || vendor.Length == 0)
        {
            EmitLogLine("The java probe returned some data we don't understand:", MessageLevel.Error);
            EmitLogLine($"java.version=\"{version}\" java.vendor=\"{vendor}\" os.arch=\"{osArch}\"", MessageLevel.Warning);
            EmitLogLine("\nMinecraft might not start properly.", MessageLevel.Launcher);
            Parent.Instance.UpdateRuntimeContext();
            EmitSucceeded();
            return;
        }

        var mojangPlatform = JavaUtils.Is64BitArchitecture(osArch) ? "64" : "32";
        PrintJavaInfo(version, mojangPlatform, osArch, vendor);
        // write the probe result into the path-keyed state cache (S5), then
        // refresh the runtime context from it — settings are out of the loop (C9)
        var signature = javaSignature;
        var detectKey = this.detectKey;
        Services.LauncherState?.Update(state =>
        {
            var detections = new Dictionary<string, JavaDetectEntry>(
                state.Java.Detections, StringComparer.Ordinal)
            {
                [detectKey] = new(
                    signature,
                    mojangPlatform,
                    osArch,
                    version,
                    vendor),
            };
            return state with { Java = state.Java with { Detections = detections } };
        });
        Parent.Instance.UpdateRuntimeContext();
        EmitSucceeded();
    }

    private void PrintJavaInfo(string version, string architecture, string realArchitecture, string vendor)
    {
        EmitLogLine($"Java is version {version}, using {architecture} ({realArchitecture}) architecture, from {vendor}",
            MessageLevel.Launcher);
    }

    /// <summary>Port of the QCryptographicHash::Sha1 over QByteArray::number(unixtime) + path.</summary>
    internal static string ComputeJavaSignature(long javaUnixTime, string javaPath)
    {
        var timestampBytes = Encoding.ASCII.GetBytes(javaUnixTime.ToString(CultureInfo.InvariantCulture));
        var pathBytes = Encoding.UTF8.GetBytes(javaPath);
        var hash = SHA1.HashData([.. timestampBytes, .. pathBytes]);
        return Convert.ToHexStringLower(hash);
    }

    public override bool CanAbort() => false;
}
