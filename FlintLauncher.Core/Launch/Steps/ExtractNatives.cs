// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
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
// Port of launcher/minecraft/launch/ExtractNatives.{h,cpp}: extract the native jars into the
// instance's natives folder (with the .jnilib -> .dylib rename hack on Java 8+).

using System.IO.Compression;
using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ExtractNatives(LaunchTask parent) : AwaitedStep(parent)
{
    protected override Task RunAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var toExtract = instance.GetNativeJars();
        if (toExtract.Count == 0)
        {
            return Task.CompletedTask;
        }

        var outputPath = instance.GetNativePath();
        FileOps.EnsureFolderPathExists(outputPath);
        foreach (var source in toExtract)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!UnzipNatives(source, outputPath.Value, instance.GetJavaLanguageVersion() >= 8))
            {
                var reason = $"Couldn't extract native jar '{source}' to destination '{outputPath}'";
                EmitLogLine(reason, MessageLevel.Fatal);
                throw new InvalidOperationException(reason);
            }
        }

        return Task.CompletedTask;
    }

    public override void FinalizeStep()
    {
        var instance = Parent.Instance;
        var targetDir = instance.InstanceRoot / "natives";
        if (targetDir.ExistsDirectory())
        {
            try
            {
                targetDir.DeleteDirectoryRecursively();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // (the C++ QDir::removeRecursively ignores failures too)
            }
        }
    }

    private static string ReplaceSuffix(string target, string suffix, string replacement)
    {
        if (!target.EndsWith(suffix, StringComparison.Ordinal))
        {
            return target;
        }

        return target[..^suffix.Length] + replacement;
    }

    private static bool UnzipNatives(AbsolutePath source, string targetFolder, bool applyJnilibHack)
    {
        try
        {
            using var zip = ZipFile.OpenRead(source.Value);
            var directory = Path.GetFullPath(targetFolder);
            FileOps.EnsureFolderPathExists(new AbsolutePath(directory));

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (applyJnilibHack)
                {
                    name = ReplaceSuffix(name, ".jnilib", ".dylib");
                }

                if (name.EndsWith('/'))
                {
                    FileOps.EnsureFolderPathExists((new AbsolutePath(directory) / name));
                    continue;
                }

                var absFilePath = new AbsolutePath(directory) / name;
                // zip-slip guard (same invariant as MMCZip::ExtractSubDir)
                if (!absFilePath.Value.StartsWith(directory, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    return false;
                }

                FileOps.EnsureFilePathExists(absFilePath);
                using var entryStream = entry.Open();
                using var output = File.Create(absFilePath.Value);
                entryStream.CopyTo(output);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
