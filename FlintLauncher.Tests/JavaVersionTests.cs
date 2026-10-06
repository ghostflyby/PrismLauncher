// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/JavaVersion_test.cpp.
using FlintLauncher.Core.Java;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaVersionTests
{
    [Theory]
    [InlineData("1.6.0_33", 6, 0, 33, "")]
    [InlineData("1.9.0_1-ea", 9, 0, 1, "ea")]
    [InlineData("9", 9, 0, 0, "")]
    [InlineData("9.1", 9, 1, 0, "")]
    [InlineData("9.0.1", 9, 0, 1, "")]
    [InlineData("9-ea", 9, 0, 0, "ea")]
    [InlineData("9.0.1-ea", 9, 0, 1, "ea")]
    public void Parse(string str, int major, int minor, int security, string prerelease)
    {
        var v = new JavaVersion(str);
        Assert.Equal(str, v.ToString());
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(security, v.Security);
        Assert.Equal(prerelease, v.Prerelease);
    }

    [Theory]
    [InlineData("1.6.0_33", "6.0.33", false, true, false)]
    [InlineData("1.5.0_33", "1.6.0_33", true, false, false)]
    [InlineData("9", "9.0.1", true, false, false)]
    [InlineData("9.0.1", "9", false, false, true)]
    [InlineData("9.1", "9.0.1", false, false, true)]
    [InlineData("9.0.1", "9.1", true, false, false)]
    [InlineData("9.1", "9", false, false, true)]
    [InlineData("9", "9.1", true, false, false)]
    [InlineData("9", "9.0", false, true, false)]
    [InlineData("9", "9.0.0", false, true, false)]
    [InlineData("9.0", "9.0.0", false, true, false)]
    [InlineData("9-ea", "9", true, false, false)]
    [InlineData("9", "9.0.1-ea", true, false, false)]
    [InlineData("9.0.1-ea", "9", false, false, true)]
    [InlineData("9-1", "9-1", false, true, false)]
    [InlineData("9-1", "9-2", true, false, false)]
    [InlineData("9-5", "9-20", true, false, false)]
    [InlineData("9-rc1", "9-rc2", true, false, false)]
    [InlineData("9-rc5", "9-rc20", true, false, false)]
    [InlineData("9-rc", "9-rc2", true, false, false)]
    [InlineData("9-ea", "9-rc", true, false, false)]
    public void Sort(string lhs, string rhs, bool smaller, bool equal, bool bigger)
    {
        var lver = new JavaVersion(lhs);
        var rver = new JavaVersion(rhs);
        Assert.Equal(smaller, lver < rver);
        Assert.Equal(equal, lver == rver);
        Assert.Equal(bigger, lver > rver);
    }

    [Theory]
    [InlineData("1.6.0_33")]
    [InlineData("1.7.0_60")]
    [InlineData("1.8.0_22")]
    [InlineData("9-ea")]
    [InlineData("9.2.4")]
    public void LegacyVersionsStillParseAfterTheJava8Floor(string version)
    {
        // the <8 support floor removed the PermGen accommodations, but version parsing
        // itself stays version-agnostic (the floor is enforced at launch, not in parsing)
        var v = new JavaVersion(version);
        Assert.True(v.Major >= 1);
    }
}
