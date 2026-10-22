// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/ModMinecraftJar.{h,cpp} plus the MMCZip::createModdedJar
// helper it is the only consumer of (launcher/MMCZip.cpp). createModdedJar lives here instead
// of IO/MMCZip.cs because it depends on the minecraft/mod Mod model.

using System.IO.Compression;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;
using TruePath;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ModMinecraftJar(LaunchTask parent) : LaunchStep(parent)
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var inst = Parent.Instance;

        // checked before GetJarMods: that accessor treats a missing profile as a broken
        // launch invariant, while this step reports it with the launch-specific message
        var components = inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return Task.CompletedTask;
        }

        if (inst.GetJarMods().Count == 0)
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        // nuke obsolete stripped jar(s) if needed
        if (!FileOps.EnsureFolderPathExists(new AbsolutePath(inst.BinRoot())))
        {
            EmitFailed("Couldn't create the bin folder for Minecraft.jar");
            return Task.CompletedTask;
        }

        var finalJarPath = inst.BinRoot() / "minecraft.jar";
        if (!RemoveJar())
        {
            EmitFailed($"Couldn't remove stale jar file: {finalJarPath}");
            return Task.CompletedTask;
        }

        // create temporary modded jar, if needed
        var jarMods = inst.GetJarMods();
        if (jarMods.Count != 0)
        {
            var mainJar = profile.GetMainJar();
            if (mainJar is null)
            {
                EmitFailed("Failed to create the custom Minecraft jar file.");
                return Task.CompletedTask;
            }

            var jars = new List<AbsolutePath>();
            var temp1 = new List<AbsolutePath>();
            var temp2 = new List<AbsolutePath>();
            var temp3 = new List<AbsolutePath>();
            mainJar.GetApplicableFiles(inst.RuntimeContext, jars, temp1, temp2, temp3, inst.GetLocalLibraryPath(),
                inst.LibraryStorageBase());
            var sourceJarPath = jars[0];
            if (!CreateModdedJar(sourceJarPath.Value, finalJarPath.Value, jarMods))
            {
                EmitFailed("Failed to create the custom Minecraft jar file.");
                return Task.CompletedTask;
            }
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override void FinalizeStep() => RemoveJar();

    private bool RemoveJar()
    {
        var inst = Parent.Instance;
        var finalJarPath = inst.BinRoot() / "minecraft.jar";
        var finalJar = new FileInfo(finalJarPath.Value);
        if (finalJar.Exists)
        {
            try
            {
                finalJar.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of MMCZip::createModdedJar: merge the jar mods into a copy of the source
    /// jar at targetJarPath. Reverse order respects the loading order of the components.</summary>
    public static bool CreateModdedJar(string sourceJarPath, string targetJarPath, IReadOnlyList<Mod> mods)
    {
        // manual dispose instead of `using`: the error branches below must close the archive
        // BEFORE deleting the half-written target file, and the finally must cover the
        // success/throw paths too (ZipArchive.Dispose is idempotent)
        ZipArchive? zipOut = null;
        try
        {
            zipOut = new ZipArchive(File.Create(targetJarPath), ZipArchiveMode.Create);
            // Files already added to the jar. These files will be skipped.
            var addedFiles = new HashSet<string>(StringComparer.Ordinal);

            // Modify the jar
            // This needs to be done in reverse-order to ensure we respect the loading order of components
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                var mod = mods[i];
                // do not merge disabled mods.
                if (!mod.Enabled)
                {
                    continue;
                }

                if (mod.Type == ResourceType.ZipFile)
                {
                    if (!MmcZip.MergeZipFiles(zipOut, mod.Fileinfo.FullName, addedFiles))
                    {
                        zipOut.Dispose();
                        FileOps.DeletePath(new AbsolutePath(targetJarPath));
                        return false;
                    }
                }
                else if (mod.Type == ResourceType.SingleFile)
                {
                    // FIXME: buggy - does not work with addedFiles
                    var filename = mod.Fileinfo;
                    AddFileToZip(zipOut, filename.FullName, filename.Name);
                    addedFiles.Add(filename.Name);
                }
                else if (mod.Type == ResourceType.Folder)
                {
                    // untested, but seems to be unused / not possible to reach
                    // FIXME: buggy - does not work with addedFiles
                    var filename = mod.Fileinfo;
                    var whatToZip = filename.FullName;
                    var parentDir = Path.GetDirectoryName(Path.GetFullPath(whatToZip)) ?? "";
                    var files = new List<string>();
                    MmcZip.CollectFileListRecursively(new AbsolutePath(whatToZip), null, files);

                    files.RemoveAll(file => addedFiles.Contains(file));

                    if (!MmcZip.CompressDirFiles(zipOut, parentDir, files))
                    {
                        zipOut.Dispose();
                        FileOps.DeletePath(new AbsolutePath(targetJarPath));
                        return false;
                    }
                }
                else
                {
                    // Make sure we do not continue launching when something is missing or undefined...
                    zipOut.Dispose();
                    FileOps.DeletePath(new AbsolutePath(targetJarPath));
                    return false;
                }
            }

            // finally add the minecraft.jar contents (everything except META-INF, so the jar
            // keeps the mod-added manifest entries)
            if (!MmcZip.MergeZipFiles(zipOut, sourceJarPath, addedFiles,
                    key => !key.Contains("META-INF", StringComparison.Ordinal)))
            {
                zipOut.Dispose();
                FileOps.DeletePath(new AbsolutePath(targetJarPath));
                return false;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or ArgumentException or NotSupportedException)
        {
            // close before deleting: an open handle keeps the half-written file alive on Windows
            zipOut?.Dispose();
            try
            {
                FileOps.DeletePath(new AbsolutePath(targetJarPath));
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // already failing; the C++ qCritical()s here
            }

            return false;
        }
        finally
        {
            // the error branches already disposed before deleting the half-written file;
            // ZipArchive.Dispose is idempotent, so the success/throw paths dispose here
            zipOut?.Dispose();
        }
    }

    /// <summary>Port of MMCZip::ArchiveWriter::addFile: store one file under the given name.</summary>
    private static void AddFileToZip(ZipArchive zip, string filePath, string entryName)
    {
        zip.CreateEntryFromFile(filePath, entryName.Replace('\\', '/'), CompressionLevel.Optimal);
    }
}
