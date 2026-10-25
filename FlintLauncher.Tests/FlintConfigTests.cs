// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core;
using Xunit;

namespace FlintLauncher.Tests;

public class FlintConfigTests
{
    [Fact]
    public void PrintableVersion_AppendsDevelopChannel()
    {
        var config = FlintConfig.Default;
        Assert.Equal("0.1.0-develop", config.PrintableVersionString());
    }

    [Fact]
    public void MetaUrl_MatchesUpstreamLayout()
    {
        Assert.Equal("https://meta.prismlauncher.org/v1/", FlintConfig.Default.MetaUrl);
    }

    [Fact]
    public void FlameApiKey_IsEmptyByDefault()
    {
        // Upstream's key is issued specifically to Prism Launcher and must not be reused
        // by derivative works (upstream CMakeLists.txt comment). Users supply their own.
        Assert.Equal(string.Empty, FlintConfig.Default.FlameApiKey);
    }

    [Fact]
    public void UserAgent_UsesFlintIdentity()
    {
        Assert.StartsWith("Flint/", FlintConfig.Default.UserAgent);
    }
}
