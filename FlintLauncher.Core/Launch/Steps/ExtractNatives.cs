// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/ExtractNatives.{h,cpp}: extract the native jars into the
// instance's natives folder (with the .jnilib -> .dylib rename hack on Java 8+).

using System.IO.Compression;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ExtractNatives : LaunchStep
{
    public ExtractNatives(LaunchTask parent) : base(parent) { }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var instance = Parent.Instance;
        var toExtract = instance.GetNativeJars();
        if (toExtract.Count == 0)
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        var outputPath = instance.GetNativePath();
        FileSystem.EnsureFolderPathExists(outputPath);
        var javaVersion = instance.GetJavaVersion();
        var jniHackEnabled = javaVersion.Major >= 8;
        foreach (var source in toExtract)
        {
            if (!UnzipNatives(source, outputPath, jniHackEnabled))
            {
                EmitLogLine($"Couldn't extract native jar '{source}' to destination '{outputPath}'", MessageLevel.Fatal);
                EmitFailed($"Couldn't extract native jar '{source}' to destination '{outputPath}'");
                return Task.CompletedTask;
            }
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override void FinalizeStep()
    {
        var instance = Parent.Instance;
        var targetDir = FileSystem.PathCombine(instance.InstanceRoot(), "natives/");
        if (Directory.Exists(targetDir))
        {
            try
            {
                Directory.Delete(targetDir, recursive: true);
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

    private static bool UnzipNatives(string source, string targetFolder, bool applyJnilibHack)
    {
        try
        {
            using var zip = ZipFile.OpenRead(source);
            var directory = Path.GetFullPath(targetFolder);
            FileSystem.EnsureFolderPathExists(directory);

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (applyJnilibHack)
                {
                    name = ReplaceSuffix(name, ".jnilib", ".dylib");
                }

                if (name.EndsWith('/'))
                {
                    FileSystem.EnsureFolderPathExists(FileSystem.PathCombine(directory, name));
                    continue;
                }

                var absFilePath = FileSystem.PathCombine(directory, name);
                // zip-slip guard (same invariant as MMCZip::ExtractSubDir)
                if (!absFilePath.StartsWith(directory, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    return false;
                }

                FileSystem.EnsureFilePathExists(absFilePath);
                using var entryStream = entry.Open();
                using var output = File.Create(absFilePath);
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
