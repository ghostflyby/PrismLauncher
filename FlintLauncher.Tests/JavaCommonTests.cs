// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for JavaCommon's JVM args sanity check (the only surviving half of the C++
// JavaCommon after the JavaCheck.jar retirement — the TestCheck flow had no production
// consumer in the headless core).

using FlintLauncher.Core.Java;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaCommonTests
{
    [Theory]
    [InlineData("-Xmx1024m", false)]
    [InlineData("-Xms256m", false)]
    [InlineData("-XX:PermSize=256m", false)]
    [InlineData("-XX-MaxHeapSize=1024m", false)]
    [InlineData("-XX:InitialHeapSize=1024m", false)]
    [InlineData("-version:17+", false)]
    [InlineData("-Dsome.property=value -XX:+UseG1GC", true)]
    [InlineData("", true)]
    public void CheckJvmArgs_RejectsMemoryAndVersionFlags(string args, bool acceptable)
    {
        Assert.Equal(acceptable, JavaCommon.CheckJvmArgs(args));
        if (!acceptable)
        {
            Assert.NotNull(JavaCommon.JvmArgsWarning(args));
        }
    }
}
