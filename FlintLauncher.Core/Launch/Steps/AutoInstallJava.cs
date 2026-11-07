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
// Flow deviations from the C++ (documented):
//  - The C++ starts the java-meta load task and then emits progressReportingRequest() without
//    ever starting the javalist load task in the "no download" branch (a latent upstream
//    hang); the port awaits both load tasks inline, so the proceed() gate is not needed here.
//  - Task/Task::Ptr plumbing (connect succeeded/failed/progress chains) collapses into
//    await + state inspection.

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Java.Download;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Tasks;
using Microsoft.Extensions.Options;
using TruePath;
using TruePath.SystemIo;
using Index = FlintLauncher.Core.Meta.Index;

namespace FlintLauncher.Core.Launch.Steps;

/// <summary>The runtime-pick/install step. The injected java root is the only layout this
/// step reads: where the launcher-managed runtimes install (the JavaDir setting under store
/// semantics, resolved by the factory at step creation). The nullable read face and javalist
/// keep the bare-fixture degrades (switches off; the "no compatible Java" warning).</summary>
public sealed class AutoInstallJava(LaunchTask parent, HttpClient http, AbsolutePath javaRoot,
    IOptionsMonitor<FlintConfigFile>? config = null, JavaInstallList? javaInstallList = null) : LaunchStep(parent)
{
    private readonly MinecraftInstance _instance = parent.Instance;
    private int _majorJavaVersionIndex;
    private readonly string _supportedArch = SysInfo.GetSupportedJavaArchitecture();

    internal override async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = _instance.Settings;
        // the typed user layer: the C++ default of this switch derives from the
        // INI-loaded JavaPath being empty (Application.cpp:768-770) and the one-shot import
        // materializes exactly that into flint.json — so a present store carries the
        // effective switch (true only for an empty/pinned-absent JavaPath), while a null
        // store keeps the bare-fixture "feature off" behavior the former unset global double
        // produced
        if (config is not { } cfg ||
            !(cfg.CurrentValue.Java.AutomaticJavaSwitch ?? false) ||
            (Variant.AsBool(settings.Get("OverrideJavaLocation")) &&
             (File.Exists(Variant.AsString(settings.Get("JavaPath"))) || Directory.Exists(Variant.AsString(settings.Get("JavaPath"))))))
        {
            return;
        }

        var packProfile = _instance.GetPackProfile();
        if (!(cfg.CurrentValue.Java.AutomaticJavaDownload ?? false))
        {
            var javas = javaInstallList;
            if (javas is not null)
            {
                try
                {
                    await javas.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // a detection failure leaves the list as-is: the walk below finds nothing
                    // compatible and the step falls back to the default Java, like the C++
                    // (which only logged the load task's failure)
                }

                var compatibleMajors = packProfile.GetProfile()?.GetCompatibleJavaMajors() ?? [];
                for (var i = 0; i < javas.Count; i++)
                {
                    var java = javas[i];
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
            return;
        }

        if (_supportedArch.Length == 0)
        {
            EmitLogLine($"Your system ({SysInfo.CurrentSystem()}-{SysInfo.UseQtForArch()}) is not compatible with automatic Java installation. Using the default Java path.",
                MessageLevel.Warning);
            return;
        }

        var wantedJavaName = packProfile.GetProfile()?.GetCompatibleJavaName() ?? "";
        if (wantedJavaName.Length == 0)
        {
            EmitLogLine("Your meta information is out of date or doesn't have the information necessary to determine what installation of " +
                        "Java should be used. Using the default Java path.", MessageLevel.Warning);
            return;
        }

        var javaDir = javaRoot;
        var relativeBinary = new LocalPath(wantedJavaName) / "bin" / JavaUtils.JavaExecutable;
        var wantedJavaPath = javaDir / relativeBinary;
        if (wantedJavaPath.Exists())
        {
            SetJavaPathFromPartial();
            return;
        }

        // load the "net.minecraft.java" version list, then walk the compatible majors
        var versionList = MetadataIndex().Get("net.minecraft.java");
        await versionList.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

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
    }

    private void SetJavaPathFromPartial()
    {
        var packProfile = _instance.GetPackProfile();
        var javaName = packProfile.GetProfile()?.GetCompatibleJavaName() ?? "";
        var javaDir = javaRoot;
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
        }
    }

    private async Task DownloadJava(MetaVersion version, string javaName, CancellationToken cancellationToken)
    {
        var runtimes = version.Data?.Runtimes ?? [];
        foreach (var java in runtimes)
        {
            if (java.RuntimeOs == _supportedArch && java.Name == javaName)
            {
                var javaDir = javaRoot;
                var finalPath = javaDir / java.Name;
                Func<IProgress<TaskProgress>?, CancellationToken, Task>? download = java.DownloadType switch
                {
                    JavaDownloadType.Manifest => (progress, token) => ManifestDownloadTask.DownloadAsync(
                        new Uri(java.Url), finalPath, http, token, progress,
                        java.ChecksumType, java.ChecksumHash),
                    JavaDownloadType.Archive => (progress, token) => ArchiveDownloadTask.DownloadAsync(
                        new Uri(java.Url), finalPath, http, token, progress,
                        java.ChecksumType, java.ChecksumHash),
                    _ => null,
                };

                if (download is null)
                {
                    try
                    {
                        if (finalPath.Exists())
                        {
                            FileOps.DeletePath(new AbsolutePath(finalPath));
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

                    EmitLogLine("Could not determine Java download type!", MessageLevel.Fatal);
                    throw new InvalidOperationException("Could not determine Java download type!");
                }

                try
                {
                    // the JDK lands first, then the macOS layout symlink runs on it
                    await download(null, cancellationToken).ConfigureAwait(false);
                    if (OperatingSystem.IsMacOS())
                    {
                        SymlinkTask.Run(finalPath);
                    }

                    SetJavaPathFromPartial();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
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
        var packProfile = _instance.GetPackProfile();
        var wantedJavaName = packProfile.GetProfile()?.GetCompatibleJavaName() ?? "";
        var majorJavaVersions = MajorJavaVersions();
        if (_majorJavaVersionIndex >= majorJavaVersions.Count)
        {
            EmitLogLine($"No versions of Java were found for your operating system: {SysInfo.CurrentSystem()}-{SysInfo.UseQtForArch()}",
                MessageLevel.Warning);
            EmitLogLine("No compatible version of Java was found. Using the default one.", MessageLevel.Warning);
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
            try
            {
                await MetadataIndex().LoadAsync("net.minecraft.java", javaMajor.Version,
                    NetMode.Online, force: false, cancellationToken).ConfigureAwait(false);
                await DownloadJava(javaMajor, wantedJavaName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await TryNextMajorJava(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override bool CanAbort() => true;
}
