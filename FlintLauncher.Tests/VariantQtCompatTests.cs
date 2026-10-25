// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Qt-read-compat pins for the Variant coercions and the ini codec's @-types:
//  - Variant.AsBool's string branch must judge strings the way QVariant(QString).toBool()
//    does (toLower(), then ONLY empty/"0"/"false" are false — everything else reads true,
//    Qt6 qmetatype.cpp convertToBool). The Override* import gates and the state keys read
//    hand-edited Prism cfg strings through it; a strict "true"/"1" match folded away values
//    Qt's own reads kept.
//  - @String(...) wrappers decode to their content the way Qt's read side unwraps them
//    (the shape a QString with an embedded NUL takes in the INI), and @ByteArray
//    stringifies as text instead of the "System.Byte[]" type name.
//  - The @@ double-prefix for plain @-prefixed values and the verbatim Raw @-types are
//    untouched behavior, pinned here against regressions.

using FlintLauncher.Core.Settings;
using Xunit;

namespace FlintLauncher.Tests;

public class VariantQtBoolTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("2", true)] // Qt: not empty/"0"/"false" → true
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData(" false ", true)] // the lowered WHOLE string is compared, no trimming
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    public void AsBool_String_Follows_QVariant_Semantics(string value, bool expected)
    {
        Assert.Equal(expected, Variant.AsBool(value));
    }

    [Fact]
    public void AsBool_NonString_Arms_AreUnchanged()
    {
        Assert.False(Variant.AsBool(null));
        Assert.True(Variant.AsBool(true));
        Assert.False(Variant.AsBool(false));
        Assert.False(Variant.AsBool(0));
        Assert.True(Variant.AsBool(5));
        Assert.False(Variant.AsBool(0L));
        Assert.True(Variant.AsBool(7L));
        Assert.False(Variant.AsBool(new object())); // unknown kinds still read false
    }

    [Fact]
    public void AsBool_Still_RoundTrips_The_Write_Side_Form()
    {
        // the write side only promises Flint's own round-trip ("true"/"false" strings and
        // real bools) — the relaxed read must not disturb it
        Assert.True(Variant.AsBool(Variant.AsString(true)));
        Assert.False(Variant.AsBool(Variant.AsString(false)));
        Assert.True(Variant.AsBool(Variant.FromIniValue(IniValue.From("true"))));
        Assert.False(Variant.AsBool(Variant.FromIniValue(IniValue.From("false"))));
    }
}

public class QSettingsIniAtTypeTests
{
    private static IniValue ParseValue(string valueText)
    {
        var map = QSettingsIniCodec.Parse("k=" + valueText + "\n");
        return map["k"];
    }

    // ------------------------------------------------------------------- @String

    [Fact]
    public void AtString_With_Embedded_Nul_Decodes_To_Its_Content()
    {
        // the shape Qt writes for a QString carrying a NUL: @String(ab\0\x63\x64)
        var value = ParseValue("@String(ab\\0\\x63\\x64)");
        Assert.Equal(IniValue.Kind.String, value.ValueKind);
        Assert.Equal("ab\0cd", value.String);
    }

    [Theory]
    [InlineData("@String(hello world)", "hello world")]
    [InlineData("@String()", "")]
    [InlineData("@String(line\\nbreak)", "line\nbreak")]
    [InlineData("@String(\\x41\\x42)", "AB")]
    public void AtString_Decodes_The_Ini_Escape_Set(string encoded, string expected)
    {
        var value = ParseValue(encoded);
        Assert.Equal(IniValue.Kind.String, value.ValueKind);
        Assert.Equal(expected, value.String);
    }

    [Fact]
    public void AtString_Through_Quotes_Still_Decodes()
    {
        // quote processing precedes type detection (Qt does the same — the quoted
        // @ByteArray entry in the golden fixtures relies on it)
        var map = QSettingsIniCodec.Parse("k=\"@String(quoted)\"\n");
        Assert.Equal("quoted", map["k"].String);
    }

    [Fact]
    public void AtString_Value_Reads_Back_Through_IniFile_GetString()
    {
        // pre-fix the wrapper stayed Raw and GetString fell back to the default; Qt's read
        // yields the plain string, and now so does ours (the ConfigVersion line selects the
        // modern parser — the pre-1.1 legacy format predates @-types)
        var file = new IniFile();
        Assert.True(file.LoadText("ConfigVersion=1.3\nk=@String(nul\\0less)\n"));
        Assert.Equal("nul\0less", file.GetString("k", "DEF"));
    }

    // ----------------------------------------------- @ByteArray stringification

    [Fact]
    public void ByteArray_AsString_Is_Text_Not_The_Type_Name()
    {
        Assert.Equal("hello world", Variant.AsString(Variant.FromIniValue(ParseValue("@ByteArray(hello world)"))));
        // valid multi-byte UTF-8 decodes as text
        Assert.Equal("中文", Variant.AsString(Variant.FromIniValue(ParseValue("@ByteArray(\\xE4\\xB8\\xAD\\xE6\\x96\\x87)"))));
        // invalid UTF-8 falls back to the lossless Latin-1 per-byte form
        Assert.Equal("\u00FF\u00FE", Variant.AsString(Variant.FromIniValue(ParseValue("@ByteArray(\\xff\\xfe)"))));
    }

    [Fact]
    public void ByteArray_AsString_Direct_Bytes_Follow_The_Same_Rule()
    {
        Assert.Equal("A中", Variant.AsString(new byte[] { 0x41, 0xE4, 0xB8, 0xAD }));
        Assert.Equal("\u00FF", Variant.AsString(new byte[] { 0xFF }));
        Assert.Equal("", Variant.AsString(Array.Empty<byte>()));
    }

    // ------------------------------- untouched neighbors, pinned against regressions

    [Fact]
    public void Plain_At_Prefix_Still_Doubles_And_Q_Types_Stay_Raw()
    {
        // '@'-prefixed ordinary values are written "@@..." by Qt and read back as one '@'
        // (the C# side must keep handling this — the @String decode must not swallow it)
        var doubled = ParseValue("@@weird");
        Assert.Equal(IniValue.Kind.String, doubled.ValueKind);
        Assert.Equal("@weird", doubled.String);

        foreach (var verbatim in new[] { "@Variant(xyz)", "@Point(1 2)", "@DateTime(2024)" })
        {
            var value = ParseValue(verbatim);
            Assert.Equal(IniValue.Kind.Raw, value.ValueKind);
            Assert.Equal(verbatim, value.String);
        }
    }
}

public class QSettingsIniKeyEscapeTests
{
    // iniUnescapedKey is per-code-unit (see QSettingsIniCodec.UnescapeKey): each %XX becomes
    // exactly one QChar, so a run of %XX escapes means Latin-1-range characters — never a
    // UTF-8 byte sequence. The golden/torture fixtures (IniFileGoldenTests) pin the rest of
    // the key grammar ([%General], %2F, %20) against the same change.

    [Fact]
    public void Percent_Xx_Run_Decodes_Per_Code_Unit_Not_As_Utf8()
    {
        // "a%C3%A9b" is a, U+00C3, U+00A9, b — the old byte-run merge folded it to "aéb"
        var map = QSettingsIniCodec.Parse("a%C3%A9b=v\n");
        Assert.Equal(new[] { "a\u00C3\u00A9b" }, map.Keys);
        Assert.Equal("v", map["a\u00C3\u00A9b"].String);
    }

    [Fact]
    public void Single_Latin1_Escape_Decodes_To_Its_Code_Point()
    {
        // %E9 -> U+00E9 "é"
        var map = QSettingsIniCodec.Parse("%E9c=v\n");
        Assert.Equal("\u00E9c", map.Keys.Single());
    }

    [Fact]
    public void Percent_U_Escapes_One_Code_Point()
    {
        // %U4E2D -> U+4E2D "中"
        var map = QSettingsIniCodec.Parse("%U4E2D=v\n");
        Assert.Equal("\u4E2D", map.Keys.Single());
        Assert.Equal("v", map["\u4E2D"].String);
    }
}
