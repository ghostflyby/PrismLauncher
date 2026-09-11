// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/Index_test.cpp.
using FluentIndex = Flint.Core.Meta.Index;
using Flint.Core.Meta;
using Xunit;

namespace Flint.Tests;

public class MetaIndexTests
{
    [Fact]
    public void Test_HasUid_And_GetList()
    {
        var windex = new FluentIndex(
        [
            new VersionList("list1"),
            new VersionList("list2"),
            new VersionList("list3"),
        ]);
        Assert.True(windex.HasUid("list1"));
        Assert.False(windex.HasUid("asdf"));
        Assert.NotNull(windex.Get("list2"));
        Assert.Equal("list2", windex.Get("list2").Uid());
        Assert.NotNull(windex.Get("adsf"));
    }

    [Fact]
    public void Test_Merge()
    {
        var windex = new FluentIndex(
        [
            new VersionList("list1"),
            new VersionList("list2"),
            new VersionList("list3"),
        ]);
        Assert.Equal(3, windex.Lists().Count);
        windex.Merge(new FluentIndex(
        [
            new VersionList("list1"),
            new VersionList("list2"),
            new VersionList("list3"),
        ]));
        Assert.Equal(3, windex.Lists().Count);
        windex.Merge(new FluentIndex(
        [
            new VersionList("list4"),
            new VersionList("list2"),
            new VersionList("list5"),
        ]));
        Assert.Equal(5, windex.Lists().Count);
        windex.Merge(new FluentIndex([new VersionList("list6")]));
        Assert.Equal(6, windex.Lists().Count);
    }
}
