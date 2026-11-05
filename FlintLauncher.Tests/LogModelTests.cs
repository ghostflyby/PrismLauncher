// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Launch;
using Xunit;

namespace FlintLauncher.Tests;

public class LogModelTests
{
    [Fact]
    public void Entries_AreIndexableInChronologicalOrder()
    {
        var log = new LogModel();
        log.Append(MessageLevel.Info, "one");
        log.Append(MessageLevel.Warning, "two");
        log.Append(MessageLevel.Error, "three");

        Assert.Equal(3, log.Count);
        Assert.Equal("one", log[0].Line);
        Assert.Equal(MessageLevel.Warning, log[1].Level);
        Assert.Equal("three", log[2].Line);
        Assert.Equal(new[] { "one", "two", "three" }, log.Select(entry => entry.Line).ToArray());
    }

    [Fact]
    public void RingBuffer_IndexerFollowsWrap()
    {
        var log = new LogModel { MaxLines = 2 };
        log.Append(MessageLevel.Info, "one");
        log.Append(MessageLevel.Info, "two");
        log.Append(MessageLevel.Info, "three");

        Assert.Equal(2, log.Count);
        Assert.Equal("two", log[0].Line);
        Assert.Equal("three", log[1].Line);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = log[2]);
    }
}
