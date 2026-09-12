// SPDX-License-Identifier: GPL-3.0-only
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
}
