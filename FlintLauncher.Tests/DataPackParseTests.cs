// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/DataPackParse_test.cpp (fixtures: TestData/DataPackParse, copied from
// tests/testdata/DataPackParse).
using FlintLauncher.Core.Minecraft.Resources;
using Xunit;

namespace FlintLauncher.Tests;

public class DataPackParseTests
{
    private static string TestDataDir() => Path.Combine(AppContext.BaseDirectory, "TestData", "DataPackParse");

    [Fact]
    public void Test_ParseZIP()
    {
        var zipDp = Path.Combine(TestDataDir(), "test_data_pack_boogaloo.zip");
        var pack = new DataPack(zipDp);

        var valid = DataPackUtils.ProcessZip(pack);

        Assert.Equal(4, pack.PackFormat);
        Assert.Equal("Some data pack 2 boobgaloo", pack.Description);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder()
    {
        var folderDp = Path.Combine(TestDataDir(), "test_folder");
        var pack = new DataPack(folderDp);

        var valid = DataPackUtils.ProcessFolder(pack);

        Assert.Equal(10, pack.PackFormat);
        Assert.Equal("Some data pack, maybe", pack.Description);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder2()
    {
        var folderDp = Path.Combine(TestDataDir(), "another_test_folder");
        var pack = new DataPack(folderDp);

        var valid = DataPackUtils.Process(pack);

        Assert.Equal(6, pack.PackFormat);
        Assert.Equal("Some data pack three, leaves on the tree", pack.Description);
        Assert.True(valid);
    }
}
