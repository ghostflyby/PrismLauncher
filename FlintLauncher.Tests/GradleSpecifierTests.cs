// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of tests/GradleSpecifier_test.cpp.

using FlintLauncher.Core.Minecraft;
using Xunit;

namespace FlintLauncher.Tests;

public class GradleSpecifierTests
{
    public static TheoryData<string> PositiveData => new()
    {
        "org.gradle.test.classifiers:service:1.0",
        "org.gradle.test.classifiers:service:1.0:jdk15",
        "org.gradle.test.classifiers:service:1.0@jar",
        "org.gradle.test.classifiers:service:1.0:jdk15@jar",
        "org.gradle.test.classifiers:service:1.0:jdk15@jar.pack.xz",
    };

    [Theory]
    [MemberData(nameof(PositiveData))]
    public void Test_Positive(string through)
    {
        var converted = new GradleSpecifier(through).Serialize();

        Assert.Equal(through, converted);
    }

    public static TheoryData<string, string> PathData => new()
    {
        { "group.id:artifact:1.0", "group/id/artifact/1.0/artifact-1.0.jar" },
        { "id.software:doom:1.666:demons@wad", "id/software/doom/1.666/doom-1.666-demons.wad" },
    };

    [Theory]
    [MemberData(nameof(PathData))]
    public void Test_Path(string spec, string expected)
    {
        var converted = new GradleSpecifier(spec).ToPath();

        Assert.Equal(expected, converted);
    }

    public static TheoryData<string> NegativeData => new()
    {
        "org:gradle.test:class:::ifiers:service:1.0::",
        "I like turtles",
        "",
        "herp.derp:artifact",
    };

    [Theory]
    [MemberData(nameof(NegativeData))]
    public void Test_Negative(string input)
    {
        var spec = new GradleSpecifier(input);
        Assert.False(spec.IsValid);
        Assert.Equal(input, spec.Serialize());
        Assert.True(string.IsNullOrEmpty(spec.ToPath()));
    }
}
