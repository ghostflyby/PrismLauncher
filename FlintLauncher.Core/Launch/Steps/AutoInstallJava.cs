// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/AutoInstallJava.{h,cpp}: pick a runtime from the java
// metadata list matching the profile's compatible java majors (and the OS/arch) and install it
// through the P5a Java::Manifest/ArchiveDownloadTask machinery.
//
// Application-singleton reach-ins resolve through the parent LaunchTask's LaunchServices
// (see the other Launch steps):
//  - APPLICATION->settings() (global, the java/download switches) -> Services.Config
//    (the typed flint.json store, D4)
//  - APPLICATION->javalist() (the "no download" branch) -> Services.JavaInstallListProvider
//    (null degrades to the upstream "no compatible Java version was found" warning)
//  - APPLICATION->javaPath() (settings "JavaDir")   -> Services.JavaRoot (the JavaDir
//    setting under E2 store semantics; falls back to the store/java anchor)
//  - APPLICATION->metadataIndex()                   -> the instance PackProfile's index
//
// Flow deviations from the C++ (documented):
//  - The C++ starts the java-meta load task and then emits progressReportingRequest() without
//    ever starting the javalist load task in the "no download" branch (a latent upstream
//    hang); the port awaits both load tasks inline, so the proceed() gate is not needed here.
//  - Task/Task::Ptr plumbing (connect succeeded/failed/progress chains) collapses into
//    await + state inspection.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Java.Download;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Tasks;
using Index = FlintLauncher.Core.Meta.Index;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class AutoInstallJava : LaunchStep
{
    private readonly MinecraftInstance _instance;
    private FlintTask? currentTask;
    private int majorJavaVersionIndex;
    private readonly string _supportedArch;

    public AutoInstallJava(LaunchTask parent) : base(parent)
    {
        _instance = Parent.Instance;
        _supportedArch = SysInfo.GetSupportedJavaArchitecture();
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = _instance.Settings;
        // the typed user layer (D4): the C++ default of this switch derives from the
        // INI-loaded JavaPath being empty (Application.cpp:768-770) and the one-shot import
        // materializes exactly that into flint.json (E3) — so a present store carries the
        // effective switch (true only for an empty/pinned-absent JavaPath), while a null
        // store keeps the bare-fixture "feature off" behavior the former unset global double
        // produced
        if (Services.Config is not { } config ||
            !(config.Current.Java.AutomaticJavaSwitch ?? false) ||
            (Variant.AsBool(settings.Get("OverrideJavaLocation")) &&
             (File.Exists(Variant.AsString(settings.Get("JavaPath"))) || Directory.Exists(Variant.AsString(settings.Get("JavaPath"))))))
        {
            EmitSucceeded();
            return;
        }

        var packProfile = _instance.GetPackProfile();
        if (!(config.Current.Java.AutomaticJavaDownload ?? false))
        {
            var javas = Services.JavaInstallListProvider?.Invoke();
            if (javas is not null)
            {
                // (upstream connects finished + emits progressReportingRequest without starting
                //  the task; the port awaits the load instead — see header note)
                currentTask = javas.GetLoadTask();
                await currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
                var compatibleMajors = packProfile.GetProfile()?.GetCompatibleJavaMajors() ?? [];
                for (var i = 0; i < javas.Count; i++)
                {
                    var java = javas.At(i);
                    if (compatibleMajors.Contains(java.Id.Major))
                    {
                        if (!java.Is64Bit)
                        {
                            EmitLogLine("The automatic Java mechanism detected a 32-bit installation of Java.", MessageLevel.Launcher);
                        }

                        SetJavaPath(java.Path);
                        return;
                    }
                }
            }

            EmitLogLine("No compatible Java version was found. Using the default one.", MessageLevel.Warning);
            EmitSucceeded();
            return;
        }

        if (_supportedArch.Length == 0)
        {
            EmitLogLine($"Your system ({SysInfo.CurrentSystem()}-{SysInfo.UseQtForArch()}) is not compatible with automatic Java installation. Using the default Java path.",
                MessageLevel.Warning);
            EmitSucceeded();
            return;
        }

        var wantedJavaName = packProfile.GetProfile()?.GetCompatibleJavaName() ?? "";
        if (wantedJavaName.Length == 0)
        {
            EmitLogLine("Your meta information is out of date or doesn't have the information necessary to determine what installation of " +
                        "Java should be used. Using the default Java path.", MessageLevel.Warning);
            EmitSucceeded();
            return;
        }

        var javaDir = Services.JavaRoot;
        var relativeBinary = FileSystem.PathCombine(wantedJavaName, "bin", JavaUtils.JavaExecutable);
        var wantedJavaPath = FileSystem.PathCombine(javaDir, relativeBinary);
        if (File.Exists(wantedJavaPath) || Directory.Exists(wantedJavaPath))
        {
            SetJavaPathFromPartial();
            return;
        }

        // load the "net.minecraft.java" version list, then walk the compatible majors
        var versionList = MetadataIndex().Get("net.minecraft.java");
        currentTask = versionList.LoadTask();
        await currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!currentTask.WasSuccessful && State == TaskState.Running)
        {
            EmitFailed(currentTask.FailReason);
            return;
        }

        await TryNextMajorJava(cancellationToken).ConfigureAwait(false);
    }

    private Index MetadataIndex() => _instance.GetPackProfile().MetadataIndex;

    private IReadOnlyList<int> MajorJavaVersions() =>
        _instance.GetPackProfile().GetProfile()?.GetCompatibleJavaMajors() ?? [];

    private void SetJavaPath(string path)
    {
        var settings = _instance.Settings;
        // C7: there is no gate boolean anymore — writing JavaPath sets the instance-layer
        // member, which IS the override (the derived OverrideJavaLocation reads true)
        settings.Set("JavaPath", path);
        settings.Set("AutomaticJava", true);
        EmitLogLine($"Compatible Java found at: {path}.", MessageLevel.Launcher);
        EmitSucceeded();
    }

    private void SetJavaPathFromPartial()
    {
        var packProfile = _instance.GetPackProfile();
        var javaName = packProfile.GetProfile()?.GetCompatibleJavaName() ?? "";
        var javaDir = Services.JavaRoot;
        // just checking if the executable is there should suffice
        // but if needed this can be achieved through refreshing the javalist
        // and retrieving the path that contains the java name
        var relativeBinary = FileSystem.PathCombine(javaName, "bin", JavaUtils.JavaExecutable);
        var finalPath = FileSystem.PathCombine(javaDir, relativeBinary);
        if (File.Exists(finalPath))
        {
            SetJavaPath(finalPath);
        }
        else
        {
            EmitLogLine("No compatible Java version was found (the binary file does not exist). Using the default one.",
                MessageLevel.Warning);
            EmitSucceeded();
        }
    }

    private async Task DownloadJava(MetaVersion version, string javaName, CancellationToken cancellationToken)
    {
        var runtimes = version.Data?.Runtimes ?? [];
        foreach (var java in runtimes)
        {
            if (java.RuntimeOs == _supportedArch && java.Name == javaName)
            {
                var javaDir = Services.JavaRoot;
                var finalPath = FileSystem.PathCombine(javaDir, java.Name);
                switch (java.DownloadType)
                {
                    case JavaDownloadType.Manifest:
                        currentTask = new ManifestDownloadTask(new Uri(java.Url), finalPath, java.ChecksumType, java.ChecksumHash);
                        break;
                    case JavaDownloadType.Archive:
                        currentTask = new ArchiveDownloadTask(new Uri(java.Url), finalPath, java.ChecksumType, java.ChecksumHash);
                        break;
                    case JavaDownloadType.Unknown:
                    default:
                        try
                        {
                            if (Directory.Exists(finalPath) || File.Exists(finalPath))
                            {
                                FileSystem.DeletePath(finalPath);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

                        EmitFailed("Could not determine Java download type!");
                        return;
                }

                if (OperatingSystem.IsMacOS())
                {
                    var seq = new SequentialTask("Install Java");
                    seq.AddTask(currentTask);
                    seq.AddTask(new SymlinkTask(finalPath));
                    currentTask = seq;
                }

                await currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
                if (currentTask.WasSuccessful)
                {
                    SetJavaPathFromPartial();
                }
                else if (IsRunning)
                {
                    // the C++ retries with the next major version on failure
                    await TryNextMajorJava(cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }

        await TryNextMajorJava(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryNextMajorJava(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return;
        }

        var packProfile = _instance.GetPackProfile();
        var wantedJavaName = packProfile.GetProfile()?.GetCompatibleJavaName() ?? "";
        var majorJavaVersions = MajorJavaVersions();
        if (majorJavaVersionIndex >= majorJavaVersions.Count)
        {
            EmitLogLine($"No versions of Java were found for your operating system: {SysInfo.CurrentSystem()}-{SysInfo.UseQtForArch()}",
                MessageLevel.Warning);
            EmitLogLine("No compatible version of Java was found. Using the default one.", MessageLevel.Warning);
            EmitSucceeded();
            return;
        }

        var majorJavaVersion = majorJavaVersions[majorJavaVersionIndex];
        majorJavaVersionIndex++;

        var versionList = MetadataIndex().Get("net.minecraft.java");
        var javaMajor = versionList.GetVersion($"java{majorJavaVersion}");

        if (javaMajor.IsLoaded)
        {
            await DownloadJava(javaMajor, wantedJavaName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            currentTask = MetadataIndex().LoadVersion("net.minecraft.java", javaMajor.Version);
            await currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
            if (currentTask.WasSuccessful)
            {
                await DownloadJava(javaMajor, wantedJavaName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TryNextMajorJava(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override bool CanAbort() => currentTask is not null && currentTask.IsRunning;

    public override bool AbortStep()
    {
        if (currentTask is not null && currentTask.IsRunning)
        {
            currentTask.Abort();
            EmitAborted();
            return true;
        }

        return base.AbortStep();
    }
}
