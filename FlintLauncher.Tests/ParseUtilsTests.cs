// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of tests/ParseUtils_test.cpp.

using FlintLauncher.Core.Minecraft;
using Xunit;

namespace FlintLauncher.Tests;

public class ParseUtilsTests
{
    public static TheoryData<string> TimestampData => new()
    {
        "2016-02-29T13:49:54+01:00",
        "2016-02-26T15:21:11+00:01",
        "2016-02-24T15:52:36+01:13",
        "2016-02-18T17:41:00+00:00",
        "2016-02-17T15:23:19+00:00",
        "2016-02-16T15:22:39+09:22",
        "2016-02-10T15:06:41+00:00",
        "2016-02-04T15:28:02-05:33",
    };

    [Theory]
    [MemberData(nameof(TimestampData))]
    public void Test_Through(string timestamp)
    {
        var timeParsed = ParseUtils.TimeFromS3Time(timestamp);
        Assert.NotNull(timeParsed);
        var timeSerialized = ParseUtils.TimeToS3Time(timeParsed.Value);

        Assert.Equal(timestamp, timeSerialized);
    }

    [Fact]
    public void ZuluSuffix_ParsesAsUtc_NotLocal()
    {
        // Qt::ISODate treats a trailing Z as +00:00. .NET honors that too (Z-terminated
        // input gets Kind=Utc even when the format matched Z as a literal); the test pins
        // that behavior against regressions.
        var parsed = ParseUtils.TimeFromS3Time("2016-02-17T15:23:19Z");

        Assert.NotNull(parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Value.Offset);
        Assert.Equal(2016, parsed.Value.Year);
        Assert.Equal(2, parsed.Value.Month);
        Assert.Equal(17, parsed.Value.Day);
        Assert.Equal(15, parsed.Value.Hour);
    }

    [Theory]
    [InlineData(5, 30, "2016-02-17T15:23:19+05:30")]
    [InlineData(-8, 0, "2016-02-17T15:23:19-08:00")]
    [InlineData(0, 0, "2016-02-17T15:23:19+00:00")]
    [InlineData(-3, -30, "2016-02-17T15:23:19-03:30")]
    public void TimeToS3Time_RendersTheOffset(int offsetHours, int offsetMinutes,
        string expected)
    {
        var time = new DateTimeOffset(2016, 2, 17, 15, 23, 19,
            new TimeSpan(offsetHours, offsetMinutes, 0));

        Assert.Equal(expected, ParseUtils.TimeToS3Time(time));
    }
}
