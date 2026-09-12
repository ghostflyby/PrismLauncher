// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/TexturePackParse_test.cpp (fixtures: TestData/TexturePackParse, copied
// from tests/testdata/TexturePackParse).
using FlintLauncher.Core.Minecraft.Resources;
using Xunit;

namespace FlintLauncher.Tests;

public class TexturePackParseTests
{
    private static string TestDataDir() => Path.Combine(AppContext.BaseDirectory, "TestData", "TexturePackParse");

    [Fact]
    public void Test_ParseZIP()
    {
        var zipRp = Path.Combine(TestDataDir(), "test_texture_pack_idk.zip");
        var pack = new TexturePack(zipRp);

        var valid = TexturePackUtils.ProcessZip(pack);

        Assert.Equal("joe biden, wake up", pack.Description);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder()
    {
        var folderRp = Path.Combine(TestDataDir(), "test_texturefolder");
        var pack = new TexturePack(folderRp);

        var valid = TexturePackUtils.ProcessFolder(pack, TexturePackProcessingLevel.BasicInfoOnly);

        Assert.Equal("Some texture pack surely", pack.Description);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder2()
    {
        var folderRp = Path.Combine(TestDataDir(), "another_test_texturefolder");
        var pack = new TexturePack(folderRp);

        var valid = TexturePackUtils.Process(pack, TexturePackProcessingLevel.BasicInfoOnly);

        Assert.Equal("quieres\nfor real", pack.Description);
        Assert.True(valid);
    }
}
