// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/steps/CheckJava.{h,cpp}: verify the configured java binary, run the
// JavaChecker when its signature changed and store the probed properties in the settings.
using System.Security.Cryptography;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Java;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class CheckJava : LaunchStep
{
    private string _javaPath = "";
    private string _javaSignature = "";
    private JavaChecker? _javaChecker;

    public CheckJava(LaunchTask parent) : base(parent)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var settings = instance.Settings();

        var javaPathSetting = Variant.AsString(settings.Get("JavaPath"));
        _javaPath = LauncherJars.ResolveExecutable(javaPathSetting);

        var perInstance = Variant.AsBool(settings.Get("OverrideJava")) || Variant.AsBool(settings.Get("OverrideJavaLocation"));

        var realJavaPath = LauncherJars.FindExecutable(_javaPath);
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
            return Task.CompletedTask;
        }

        EmitLogLine("Java path is:\n  " + _javaPath, MessageLevel.Launcher);

        if (JavaUtils.GetJavaCheckPath().Length == 0)
        {
            EmitLogLine("Java checker library could not be found. Please check your installation.", MessageLevel.Fatal);
            EmitFailed("Java checker library could not be found. Please check your installation.");
            return Task.CompletedTask;
        }

        var javaInfo = new FileInfo(realJavaPath);
        var javaUnixTime = new DateTimeOffset(javaInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var storedSignature = Variant.AsString(settings.Get("JavaSignature"));
        var storedArchitecture = Variant.AsString(settings.Get("JavaArchitecture"));
        var storedRealArchitecture = Variant.AsString(settings.Get("JavaRealArchitecture"));
        var storedVersion = Variant.AsString(settings.Get("JavaVersion"));
        var storedVendor = Variant.AsString(settings.Get("JavaVendor"));

        _javaSignature = ComputeJavaSignature(javaUnixTime, _javaPath);

        // if timestamps are not the same, or something is missing, check!
        if (_javaSignature != storedSignature || storedVersion.Length == 0 || storedArchitecture.Length == 0 ||
            storedRealArchitecture.Length == 0 || storedVendor.Length == 0)
        {
            _javaChecker = new JavaChecker(realJavaPath, "", 0, 0, 0, 0);
            EmitLogLine("Checking Java version...", MessageLevel.Launcher);
            _javaChecker.CheckFinished += CheckJavaFinished;
            _ = _javaChecker.StartAsync(cancellationToken);
            return Task.CompletedTask;
        }

        var verString = Variant.AsString(instance.Settings().Get("JavaVersion"));
        var archString = Variant.AsString(instance.Settings().Get("JavaArchitecture"));
        var realArchString = Variant.AsString(settings.Get("JavaRealArchitecture"));
        var vendorString = Variant.AsString(instance.Settings().Get("JavaVendor"));
        PrintJavaInfo(verString, archString, realArchString, vendorString);
        Parent.Instance.UpdateRuntimeContext();
        EmitSucceeded();
        return Task.CompletedTask;
    }

    private void CheckJavaFinished(JavaChecker.Result result)
    {
        switch (result.Validity)
        {
            case JavaChecker.Validity.Errored:
                {
                    // Error message displayed if java can't start
                    EmitLogLine("Could not start java:", MessageLevel.Error);
                    EmitLogLines(result.ErrorLog.Split('\n'), MessageLevel.Error);
                    EmitLogLine("\nCheck your Java settings.", MessageLevel.Launcher);
                    EmitFailed("Could not start java!");
                    return;
                }
            case JavaChecker.Validity.ReturnedInvalidData:
                {
                    EmitLogLine("Java checker returned some invalid data we don't understand:", MessageLevel.Error);
                    EmitLogLines(result.OutLog.Split('\n'), MessageLevel.Warning);
                    EmitLogLine("\nMinecraft might not start properly.", MessageLevel.Launcher);
                    Parent.Instance.UpdateRuntimeContext();
                    EmitSucceeded();
                    return;
                }
            case JavaChecker.Validity.Valid:
                {
                    var instance = Parent.Instance;
                    PrintJavaInfo(result.JavaVersion.ToString(), result.MojangPlatform, result.RealPlatform, result.JavaVendor);
                    instance.Settings().Set("JavaVersion", result.JavaVersion.ToString());
                    instance.Settings().Set("JavaArchitecture", result.MojangPlatform);
                    instance.Settings().Set("JavaRealArchitecture", result.RealPlatform);
                    instance.Settings().Set("JavaVendor", result.JavaVendor);
                    instance.Settings().Set("JavaSignature", _javaSignature);
                    Parent.Instance.UpdateRuntimeContext();
                    EmitSucceeded();
                    return;
                }
            default:
                return;
        }
    }

    private void PrintJavaInfo(string version, string architecture, string realArchitecture, string vendor)
    {
        EmitLogLine($"Java is version {version}, using {architecture} ({realArchitecture}) architecture, from {vendor}",
            MessageLevel.Launcher);
    }

    /// <summary>Port of the QCryptographicHash::Sha1 over QByteArray::number(unixtime) + path.</summary>
    internal static string ComputeJavaSignature(long javaUnixTime, string javaPath)
    {
        var timestampBytes = System.Text.Encoding.ASCII.GetBytes(javaUnixTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(javaPath);
        var hash = SHA1.HashData([.. timestampBytes, .. pathBytes]);
        return Convert.ToHexStringLower(hash);
    }

    public override bool CanAbort() => false;
}
