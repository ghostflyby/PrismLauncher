// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The Flame import classifies a manifest file it downloads: the type the pack declares, or —
// when the pack declares none — the type its contents point at.

using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using Xunit;

namespace FlintLauncher.Tests;

public class FlameResourceClassificationTests
{
    private static string Source() => Path.Combine(AppContext.BaseDirectory, "TestData", "WorldSaveParse");

    private static FlameFile ManifestFile(string fileName, ResourceType declared)
    {
        var file = new FlameFile { ProjectId = 1, FileId = 2 };
        file.Pack.AddonId = "1";
        file.Pack.ResourceType = declared;
        file.Version = new IndexedVersion { FileName = fileName };
        return file;
    }

    [Fact]
    public void DeclaredType_Wins_Over_Contents()
    {
        // the pack says shaderpack; the file on disk happens to be a world save, and the
        // declaration is what counts
        var files = new[] { ManifestFile("minecraft_save_1.zip", ResourceType.ShaderPack) };

        Assert.Equal(ResourceType.ShaderPack,
            FlameCreationTask.OtherResourcesType("minecraft_save_1.zip", files,
                Path.Combine(Source(), "minecraft_save_1.zip")));
    }

    [Fact]
    public void UndeclaredType_Falls_Back_To_Content_Detection()
    {
        var files = new[] { ManifestFile("minecraft_save_1.zip", ResourceType.Unknown) };

        Assert.Equal(ResourceType.World,
            FlameCreationTask.OtherResourcesType("minecraft_save_1.zip", files,
                Path.Combine(Source(), "minecraft_save_1.zip")));
    }

    [Fact]
    public void Undeclared_NonWorld_Stays_Unknown()
    {
        var loose = Path.Combine(Path.GetTempPath(), $"flint-not-a-world-{Guid.NewGuid():N}.zip");
        File.WriteAllText(loose, "not an archive");
        try
        {
            var files = new[] { ManifestFile("not-a-world.zip", ResourceType.Unknown) };

            Assert.Equal(ResourceType.Unknown,
                FlameCreationTask.OtherResourcesType("not-a-world.zip", files, loose));
        }
        finally
        {
            File.Delete(loose);
        }
    }

    [Fact]
    public void FileAbsentFromTheManifest_Is_Detected_By_Content()
    {
        // a file the manifest does not list has no declaration to read
        Assert.Equal(ResourceType.World,
            FlameCreationTask.OtherResourcesType("minecraft_save_3", [], Path.Combine(Source(), "minecraft_save_3")));
    }
}
