// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/ModMinecraftJar.{h,cpp} plus the MMCZip::createModdedJar
// helper it is the only consumer of (launcher/MMCZip.cpp). createModdedJar lives here instead
// of IO/MMCZip.cs because it depends on the minecraft/mod Mod model.

using System.IO.Compression;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ModMinecraftJar : LaunchStep
{
    public ModMinecraftJar(LaunchTask parent) : base(parent) { }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var inst = Parent.Instance;

        if (inst.GetJarMods().Count == 0)
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        // nuke obsolete stripped jar(s) if needed
        if (!FileSystem.EnsureFolderPathExists(inst.BinRoot()))
        {
            EmitFailed("Couldn't create the bin folder for Minecraft.jar");
            return Task.CompletedTask;
        }

        var finalJarPath = FileSystem.PathCombine(inst.BinRoot(), "minecraft.jar");
        if (!RemoveJar())
        {
            EmitFailed($"Couldn't remove stale jar file: {finalJarPath}");
            return Task.CompletedTask;
        }

        // create temporary modded jar, if needed
        var components = inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return Task.CompletedTask;
        }

        var jarMods = inst.GetJarMods();
        if (jarMods.Count != 0)
        {
            var mainJar = profile.GetMainJar();
            if (mainJar is null)
            {
                EmitFailed("Failed to create the custom Minecraft jar file.");
                return Task.CompletedTask;
            }

            var jars = new List<string>();
            var temp1 = new List<string>();
            var temp2 = new List<string>();
            var temp3 = new List<string>();
            mainJar.GetApplicableFiles(inst.RuntimeContext, jars, temp1, temp2, temp3, inst.GetLocalLibraryPath(),
                inst.LibraryStorageBase());
            var sourceJarPath = jars[0];
            if (!CreateModdedJar(sourceJarPath, finalJarPath, jarMods))
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
        var finalJarPath = FileSystem.PathCombine(inst.BinRoot(), "minecraft.jar");
        var finalJar = new FileInfo(finalJarPath);
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
        try
        {
            using var zipOut = new ZipArchive(File.Create(targetJarPath), ZipArchiveMode.Create);
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
                        FileSystem.DeletePath(targetJarPath);
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
                    MmcZip.CollectFileListRecursively(whatToZip, null, files);

                    files.RemoveAll(file => addedFiles.Contains(file));

                    if (!MmcZip.CompressDirFiles(zipOut, parentDir, files))
                    {
                        zipOut.Dispose();
                        FileSystem.DeletePath(targetJarPath);
                        return false;
                    }
                }
                else
                {
                    // Make sure we do not continue launching when something is missing or undefined...
                    zipOut.Dispose();
                    FileSystem.DeletePath(targetJarPath);
                    return false;
                }
            }

            // finally add the minecraft.jar contents (everything except META-INF, so the jar
            // keeps the mod-added manifest entries)
            if (!MmcZip.MergeZipFiles(zipOut, sourceJarPath, addedFiles,
                    key => !key.Contains("META-INF", StringComparison.Ordinal)))
            {
                zipOut.Dispose();
                FileSystem.DeletePath(targetJarPath);
                return false;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or ArgumentException or NotSupportedException)
        {
            try
            {
                FileSystem.DeletePath(targetJarPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // already failing; the C++ qCritical()s here
            }

            return false;
        }
    }

    /// <summary>Port of MMCZip::ArchiveWriter::addFile: store one file under the given name.</summary>
    private static void AddFileToZip(ZipArchive zip, string filePath, string entryName)
    {
        zip.CreateEntryFromFile(filePath, entryName.Replace('\\', '/'), CompressionLevel.Optimal);
    }
}
