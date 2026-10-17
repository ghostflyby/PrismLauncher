// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/ResourcePackParse_test.cpp (fixtures: TestData/ResourcePackParse, copied
// from tests/testdata/Resources — the upstream QFINDTESTDATA("testdata/Resources") dir).

using FlintLauncher.Core.Minecraft.Resources;
using Xunit;

namespace FlintLauncher.Tests;

public class ResourcePackParseTests
{
    private static string TestDataDir() => Path.Combine(AppContext.BaseDirectory, "TestData", "ResourcePackParse");

    [Fact]
    public void Test_ParseZIP()
    {
        var zipRp = Path.Combine(TestDataDir(), "test_resource_pack_idk.zip");
        var pack = new ResourcePack(zipRp);

        var valid = DataPackUtils.ProcessZip(pack, DataPackProcessingLevel.BasicInfoOnly);

        Assert.Equal(3, pack.PackFormat);
        Assert.Equal("um dois, feijão com arroz, três quatro, feijão no prato, cinco seis, café inglês, " +
                     "sete oito, comer biscoito, nove dez comer pastéis!!", pack.Description);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder()
    {
        var folderRp = Path.Combine(TestDataDir(), "test_folder");
        var pack = new ResourcePack(folderRp);

        var valid = DataPackUtils.ProcessFolder(pack, DataPackProcessingLevel.BasicInfoOnly);

        Assert.Equal(1, pack.PackFormat);
        Assert.Equal("Some resource pack maybe", pack.Description);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder2()
    {
        var folderRp = Path.Combine(TestDataDir(), "another_test_folder");
        var pack = new ResourcePack(folderRp);

        var valid = DataPackUtils.Process(pack, DataPackProcessingLevel.BasicInfoOnly);

        Assert.Equal(6, pack.PackFormat);
        Assert.Equal("o quartel pegou fogo, policia deu sinal, acode acode acode a bandeira nacional", pack.Description);
        Assert.True(valid); // no assets dir but it is still valid based on https://minecraft.wiki/w/Resource_pack
    }

    /// <summary>
    /// Extra check for the ported pack_format → McVersion mapping tables (the C++
    /// ResourcePack::mappings / DataPack::packFormatStr logic).
    /// </summary>
    [Fact]
    public void Test_PackFormatStr()
    {
        var zipRp = Path.Combine(TestDataDir(), "test_resource_pack_idk.zip");
        var pack = new ResourcePack(zipRp);
        DataPackUtils.ProcessZip(pack, DataPackProcessingLevel.BasicInfoOnly);

        // format 3 maps to [1.11, 1.12.2] in the resource pack table
        Assert.Equal("3 (1.11 - 1.12.2)", pack.PackFormatStr());

        var unknown = new ResourcePack(zipRp);
        unknown.SetPackFormat(123456, (0, 0), (0, 0));
        Assert.Equal("123456", unknown.PackFormatStr());

        var unrecognized = new ResourcePack(zipRp);
        unrecognized.SetPackFormat(0, (0, 0), (0, 0));
        Assert.Equal("Unrecognized", unrecognized.PackFormatStr());
    }
}
