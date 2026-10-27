// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The auth timestamp reader: token issue/expiry instants are compared against the current
// instant, so how a source's stated offset is interpreted decides token freshness.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Auth;
using Xunit;

namespace FlintLauncher.Tests;

public class AuthTimestampParsingTests
{
    private static JsonNode Value(string text) => JsonValue.Create(text);

    [Fact]
    public void StatedOffsetIsKeptAsTheInstantItNames()
    {
        Assert.True(Parsers.GetDateTimeOffset(Value("2020-12-07T19:52:08.4463796Z"), out var utc));
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(1607370728, utc.ToUnixTimeSeconds());

        Assert.True(Parsers.GetDateTimeOffset(Value("2020-12-07T21:52:08+02:00"), out var shifted));
        // the same instant, stated in another zone
        Assert.Equal(utc.ToUnixTimeSeconds(), shifted.ToUnixTimeSeconds());
        Assert.Equal(TimeSpan.FromHours(2), shifted.Offset);
    }

    [Fact]
    public void TimestampWithoutAnOffsetIsReadAsUtc_NotLocal()
    {
        // the machine's own zone must not decide when a token expires
        Assert.True(Parsers.GetDateTimeOffset(Value("2020-12-07T19:52:08.4463796"), out var parsed));

        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(1607370728, parsed.ToUnixTimeSeconds());
    }

    [Fact]
    public void NonTextAndUnparseableValuesAreRejected()
    {
        Assert.False(Parsers.GetDateTimeOffset(JsonValue.Create(42), out _));
        Assert.False(Parsers.GetDateTimeOffset(Value("not a timestamp"), out _));
        Assert.False(Parsers.GetDateTimeOffset(null, out _));
    }
}
