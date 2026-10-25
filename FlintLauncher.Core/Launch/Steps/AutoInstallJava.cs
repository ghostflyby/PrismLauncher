// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
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
// Port of launcher/minecraft/launch/AutoInstallJava.{h,cpp}: pick a runtime from the java
// metadata list matching the profile's compatible java majors (and the OS/arch) and install it
// through the Java::Manifest/ArchiveDownloadTask machinery.
//
// Application-singleton reach-ins resolve through the parent LaunchTask's LaunchServices
// (see the other Launch steps):
//  - APPLICATION->settings() (global, the java/download switches) -> Services.Config
//    (the typed flint.json store)
//  - APPLICATION->javalist() (the "no download" branch) -> Services.JavaInstallListProvider
//    (null degrades to the upstream "no compatible Java version was found" warning)
//  - APPLICATION->javaPath() (settings "JavaDir")   -> Services.JavaRoot (the JavaDir
//    setting under store semantics; falls back to the store/java anchor)
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
using TruePath;
using TruePath.SystemIo;
using Index = FlintLauncher.Core.Meta.Index;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class AutoInstallJava : LaunchStep
{
    private readonly MinecraftInstance _instance;
    private FlintTask? _currentTask;
    private int _majorJavaVersionIndex;
    private readonly string _supportedArch;

    public AutoInstallJava(LaunchTask parent) : base(parent)
    {
        _instance = Parent.Instance;
        _supportedArch = SysInfo.GetSupportedJavaArchitecture();
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = _instance.Settings;
        // the typed user layer: the C++ default of this switch derives from the
        // INI-loaded JavaPath being empty (Application.cpp:768-770) and the one-shot import
        // materializes exactly that into flint.json — so a present store carries the
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
                _currentTask = javas.GetLoadTask();
                await _currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
                var compatibleMajors = packProfile.GetProfile()?.GetCompatibleJavaMajors() ?? [];
                for (var i = 0; i < javas.Count; i++)
                {
                    var java = javas.At(i);
                    if (java.LanguageVersion is { } languageVersion && compatibleMajors.Contains(languageVersion))
                    {
                        if (!java.Is64Bit)
                        {
                            EmitLogLine("The automatic Java mechanism detected a 32-bit installation of Java.", MessageLevel.Launcher);
                        }

                        SetJavaPath(new AbsolutePath(java.Path));
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
        var relativeBinary = new LocalPath(wantedJavaName) / "bin" / JavaUtils.JavaExecutable;
        var wantedJavaPath = javaDir / relativeBinary;
        if (wantedJavaPath.Exists())
        {
            SetJavaPathFromPartial();
            return;
        }

        // load the "net.minecraft.java" version list, then walk the compatible majors
        var versionList = MetadataIndex().Get("net.minecraft.java");
        _currentTask = versionList.LoadTask();
        await _currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!_currentTask.WasSuccessful && State == TaskState.Running)
        {
            EmitFailed(_currentTask.FailReason);
            return;
        }

        await TryNextMajorJava(cancellationToken).ConfigureAwait(false);
    }

    private Index MetadataIndex() => _instance.GetPackProfile().MetadataIndex;

    private IReadOnlyList<int> MajorJavaVersions() =>
        _instance.GetPackProfile().GetProfile()?.GetCompatibleJavaMajors() ?? [];

    private void SetJavaPath(AbsolutePath path)
    {
        var settings = _instance.Settings;
        // there is no gate boolean anymore — writing JavaPath sets the instance-layer
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
        var relativeBinary = new LocalPath(javaName) / "bin" / JavaUtils.JavaExecutable;
        var finalPath = javaDir / relativeBinary;
        if (finalPath.ExistsFile())
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
                var finalPath = (AbsolutePath)javaDir / java.Name;
                switch (java.DownloadType)
                {
                    case JavaDownloadType.Manifest:
                        _currentTask = new ManifestDownloadTask(new Uri(java.Url), finalPath, Services.Net,
                            java.ChecksumType, java.ChecksumHash);
                        break;
                    case JavaDownloadType.Archive:
                        _currentTask = new ArchiveDownloadTask(new Uri(java.Url), finalPath, Services.Net,
                            java.ChecksumType, java.ChecksumHash);
                        break;
                    case JavaDownloadType.Unknown:
                    default:
                        try
                        {
                            if (finalPath.Exists())
                            {
                                FileOps.DeletePath(new AbsolutePath(finalPath));
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

                        EmitFailed("Could not determine Java download type!");
                        return;
                }

                if (OperatingSystem.IsMacOS())
                {
                    var seq = new SequentialTask("Install Java");
                    seq.AddTask(_currentTask);
                    seq.AddTask(new SymlinkTask(finalPath));
                    _currentTask = seq;
                }

                await _currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
                if (_currentTask.WasSuccessful)
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
        if (_majorJavaVersionIndex >= majorJavaVersions.Count)
        {
            EmitLogLine($"No versions of Java were found for your operating system: {SysInfo.CurrentSystem()}-{SysInfo.UseQtForArch()}",
                MessageLevel.Warning);
            EmitLogLine("No compatible version of Java was found. Using the default one.", MessageLevel.Warning);
            EmitSucceeded();
            return;
        }

        var majorJavaVersion = majorJavaVersions[_majorJavaVersionIndex];
        _majorJavaVersionIndex++;

        var versionList = MetadataIndex().Get("net.minecraft.java");
        var javaMajor = versionList.GetVersion($"java{majorJavaVersion}");

        if (javaMajor.IsLoaded)
        {
            await DownloadJava(javaMajor, wantedJavaName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _currentTask = MetadataIndex().LoadVersion("net.minecraft.java", javaMajor.Version);
            await _currentTask.StartAsync(cancellationToken).ConfigureAwait(false);
            if (_currentTask.WasSuccessful)
            {
                await DownloadJava(javaMajor, wantedJavaName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TryNextMajorJava(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override bool CanAbort() => _currentTask is not null && _currentTask.IsRunning;

    public override bool AbortStep()
    {
        if (_currentTask is not null && _currentTask.IsRunning)
        {
            _currentTask.Abort();
            EmitAborted();
            return true;
        }

        return base.AbortStep();
    }
}
