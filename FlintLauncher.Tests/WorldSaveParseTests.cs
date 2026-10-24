// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Rachel Powers <508861+Ryex@users.noreply.github.com>
//
// Port of tests/WorldSaveParse_test.cpp (fixtures: TestData/WorldSaveParse, copied from
// tests/testdata/WorldSaveParse).

using FlintLauncher.Core.Minecraft.Worlds;
using Xunit;

namespace FlintLauncher.Tests;

public class WorldSaveParseTests
{
    private static string Source() => Path.Combine(AppContext.BaseDirectory, "TestData", "WorldSaveParse");

    [Fact]
    public void Test_ParseZIP()
    {
        var zipWs = Path.Combine(Source(), "minecraft_save_1.zip");
        var save = new WorldSave(zipWs);

        var valid = WorldSaveIdentification.ProcessZip(save);

        Assert.Equal(WorldSaveFormat.Single, save.SaveFormat);
        Assert.Equal("world_1", save.SaveDirName);
        Assert.True(valid);
    }

    [Fact]
    public void Test_Parse_ZIP2()
    {
        var zipWs = Path.Combine(Source(), "minecraft_save_2.zip");
        var save = new WorldSave(zipWs);

        var valid = WorldSaveIdentification.ProcessZip(save);

        Assert.Equal(WorldSaveFormat.Multi, save.SaveFormat);
        Assert.Equal("world_2", save.SaveDirName);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder()
    {
        var folderWs = Path.Combine(Source(), "minecraft_save_3");
        var save = new WorldSave(folderWs);

        var valid = WorldSaveIdentification.ProcessFolder(save);

        Assert.Equal(WorldSaveFormat.Single, save.SaveFormat);
        Assert.Equal("world_3", save.SaveDirName);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder2()
    {
        var folderWs = Path.Combine(Source(), "minecraft_save_4");
        var save = new WorldSave(folderWs);

        var valid = WorldSaveIdentification.Process(save);

        Assert.Equal(WorldSaveFormat.Multi, save.SaveFormat);
        Assert.Equal("world_4", save.SaveDirName);
        Assert.True(valid);
    }

    [Fact]
    public void Test_Validate()
    {
        Assert.True(WorldSaveIdentification.Validate(Path.Combine(Source(), "minecraft_save_1.zip")));
        Assert.True(WorldSaveIdentification.Validate(Path.Combine(Source(), "minecraft_save_3")));
        Assert.False(WorldSaveIdentification.Validate(Path.Combine(Source(), "missing.zip")));
    }
}
