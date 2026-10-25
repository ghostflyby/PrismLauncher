// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The settings-layer dynamic-JSON text helpers (SettingsJson — the former JsonUtils tail,
// relocated next to its consumers; the deleted Require*/Ensure* node family's number-kind
// pins retired with it, docs/JSON-MIGRATION.md).

using FlintLauncher.Core.Settings;
using Xunit;

namespace FlintLauncher.Tests;

public class SettingsJsonTests
{
    [Fact]
    public void ToStringList_All_Or_Nothing()
    {
        Assert.Equal(["a", "b"], SettingsJson.ToStringList("""["a","b"]"""));
        Assert.Equal([""], SettingsJson.ToStringList("""[""]"""));
        // any parse or element error yields an empty list
        Assert.Empty(SettingsJson.ToStringList("not json"));
        Assert.Empty(SettingsJson.ToStringList("""{"a":1}"""));
        Assert.Empty(SettingsJson.ToStringList("""["a",1]"""));
    }

    [Fact]
    public void ToMap_Textual_Scalars_Compound_Nested()
    {
        var map = SettingsJson.ToMap("""{"a":"b","n":42,"t":true,"o":{"x":1}}""");
        Assert.Equal(4, map.Count);
        Assert.Equal("b", map["a"]);
        Assert.Equal("42", map["n"]);
        Assert.Equal("true", map["t"]);
        Assert.Equal("""{"x":1}""", map["o"]);
        Assert.Empty(SettingsJson.ToMap("not json"));
        Assert.Empty(SettingsJson.ToMap("[1]"));
    }
}
