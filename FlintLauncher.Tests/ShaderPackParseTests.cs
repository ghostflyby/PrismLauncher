// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Rachel Powers <508861+Ryex@users.noreply.github.com>
//
// Port of tests/ShaderPackParse_test.cpp (fixtures: TestData/ShaderPackParse, copied
// from tests/testdata/ShaderPackParse).

using FlintLauncher.Core.Minecraft.Resources;
using Xunit;

namespace FlintLauncher.Tests;

public class ShaderPackParseTests
{
    private static string TestDataDir() => Path.Combine(AppContext.BaseDirectory, "TestData", "ShaderPackParse");

    [Fact]
    public void Test_ParseZIP()
    {
        var zipSp = Path.Combine(TestDataDir(), "shaderpack1.zip");
        var pack = new ShaderPack(zipSp);

        var valid = ShaderPackUtils.ProcessZip(pack);

        Assert.Equal(ShaderPackFormat.Valid, pack.PackFormat);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseFolder()
    {
        var folderSp = Path.Combine(TestDataDir(), "shaderpack2");
        var pack = new ShaderPack(folderSp);

        var valid = ShaderPackUtils.ProcessFolder(pack);

        Assert.Equal(ShaderPackFormat.Valid, pack.PackFormat);
        Assert.True(valid);
    }

    [Fact]
    public void Test_ParseZIP2()
    {
        var folderSp = Path.Combine(TestDataDir(), "shaderpack3.zip");
        var pack = new ShaderPack(folderSp);

        var valid = ShaderPackUtils.Process(pack);

        Assert.Equal(ShaderPackFormat.Invalid, pack.PackFormat);
        Assert.True(!valid);
    }
}
