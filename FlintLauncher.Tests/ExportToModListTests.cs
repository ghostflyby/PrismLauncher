// SPDX-License-Identifier: GPL-3.0-only
// Tests for the mod-list export: the exact templates of the C++ ExportToModList for the
// HTML/Markdown/plain-text/JSON/CSV formats (with every OptionalData flag) and the custom
// line-template format, including the metadata-version fallback and the escaping rules.

using System.Text.Json;
using FlintLauncher.Core.ModPlatform.Helpers;
using Xunit;

namespace FlintLauncher.Tests;

public class ExportToModListTests
{
    private static readonly ModListMod Jei = new()
    {
        Name = "jei",
        ModId = "238222",
        Url = "https://modrinth.com/mod/jei",
        Version = "11.6.0.1016",
        Authors = ["mezz"],
        FileName = "jei-1.20.1-forge-11.6.0.1016.jar",
    };

    private static readonly ModListMod Addons = new()
    {
        Name = "JEI <addons> & \"stuff\"",
        ModId = "abc",
        Url = "",
        Version = "",
        MetadataVersion = "1.2.3", // the .pw.toml metadata version fallback
        Authors = ["author one", "author two"],
        FileName = "addons.jar",
    };

    private static readonly IReadOnlyList<ModListMod> Mods = [Jei, Addons];

    private const OptionalData All =
        OptionalData.Authors | OptionalData.Url | OptionalData.Version | OptionalData.FileName;

    // ----------------------------------------------------------------------- HTML

    [Fact]
    public void Html_AllFlags()
    {
        var expected = "<html><body><ul>\n\t" +
                       "<li><a href=\"https://modrinth.com/mod/jei\">jei</a> [11.6.0.1016] by mezz " +
                       "(jei-1.20.1-forge-11.6.0.1016.jar)</li>\n\t" +
                       "<li>JEI &lt;addons&gt; &amp; &quot;stuff&quot; [1.2.3] by author one, author two (addons.jar)</li>" +
                       "\n</ul></body></html>";

        Assert.Equal(expected, ExportToModList.ToHtml(Mods, All));
    }

    [Fact]
    public void Html_NoFlags_JustEscapedNames()
    {
        var expected = "<html><body><ul>\n\t<li>jei</li>\n\t" +
                       "<li>JEI &lt;addons&gt; &amp; &quot;stuff&quot;</li>\n</ul></body></html>";

        Assert.Equal(expected, ExportToModList.ToHtml(Mods, OptionalData.None));
    }

    // ------------------------------------------------------------------- Markdown

    [Fact]
    public void Markdown_AllFlags_EscapesSpecials()
    {
        var expected = "- [jei](https://modrinth.com/mod/jei) [11\\.6\\.0\\.1016] by mezz " +
                       "(jei\\-1\\.20\\.1\\-forge\\-11\\.6\\.0\\.1016\\.jar)\n" +
                       "- JEI \\<addons\\> & \"stuff\" [1\\.2\\.3] by author one, author two (addons\\.jar)";

        Assert.Equal(expected, ExportToModList.ToMarkdown(Mods, All));
    }

    // ------------------------------------------------------------------ plain text

    [Fact]
    public void PlainTxt_AllFlags_NoEscaping()
    {
        var expected = "jei (https://modrinth.com/mod/jei) [11.6.0.1016] by mezz " +
                       "(jei-1.20.1-forge-11.6.0.1016.jar)\n" +
                       "JEI <addons> & \"stuff\" [1.2.3] by author one, author two (addons.jar)";

        Assert.Equal(expected, ExportToModList.ToPlainTxt(Mods, All));
    }

    // ----------------------------------------------------------------------- JSON

    [Fact]
    public void Json_AllFlags_Value_Shape()
    {
        // the Qt byte shape (sorted keys, 4-space indent) is retired; the member set and
        // values of the shared export are the contract (the empty-url second mod carries no
        // url member even with the flag on)
        using var doc = JsonDocument.Parse(ExportToModList.ToJson(Mods, All));
        var array = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, array.ValueKind);

        var first = array[0];
        Assert.Equal("jei", first.GetProperty("name").GetString());
        Assert.Equal("https://modrinth.com/mod/jei", first.GetProperty("url").GetString());
        Assert.Equal("11.6.0.1016", first.GetProperty("version").GetString());
        Assert.Equal("jei-1.20.1-forge-11.6.0.1016.jar", first.GetProperty("filename").GetString());
        Assert.Equal(["mezz"], first.GetProperty("authors").EnumerateArray()
            .Select(a => a.GetString()).ToArray());

        var second = array[1];
        Assert.Equal("JEI <addons> & \"stuff\"", second.GetProperty("name").GetString());
        Assert.False(second.TryGetProperty("url", out _));
        Assert.Equal("1.2.3", second.GetProperty("version").GetString());
        Assert.Equal(["author one", "author two"], second.GetProperty("authors").EnumerateArray()
            .Select(a => a.GetString()).ToArray());
        Assert.Equal("addons.jar", second.GetProperty("filename").GetString());
    }

    [Fact]
    public void Json_NameOnly()
    {
        using var doc = JsonDocument.Parse(ExportToModList.ToJson(Mods, OptionalData.None));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var members = entry.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(["name"], members);
        }

        Assert.Equal("jei", doc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("JEI <addons> & \"stuff\"", doc.RootElement[1].GetProperty("name").GetString());
    }

    [Fact]
    public void Json_EmptyList_RendersEmptyArray()
    {
        Assert.Equal("[]\n", ExportToModList.ToJson([], OptionalData.None));
    }

    // ----------------------------------------------------------------------- CSV

    [Fact]
    public void Csv_AllFlags_QuotesMultiAuthor()
    {
        var expected = "jei,https://modrinth.com/mod/jei,11.6.0.1016,mezz,jei-1.20.1-forge-11.6.0.1016.jar\n" +
                       "JEI <addons> & \"stuff\",,1.2.3,\"author one,author two\",addons.jar";

        Assert.Equal(expected, ExportToModList.ToCsv(Mods, All));
    }

    // --------------------------------------------------------------- dispatchers

    [Fact]
    public void ExportDispatchesToEachFormat()
    {
        Assert.Equal(ExportToModList.ToHtml(Mods, All), ExportToModList.ExportModsToString(Mods, ModListFormat.Html, All));
        Assert.Equal(ExportToModList.ToMarkdown(Mods, All), ExportToModList.ExportModsToString(Mods, ModListFormat.Markdown, All));
        Assert.Equal(ExportToModList.ToPlainTxt(Mods, All), ExportToModList.ExportModsToString(Mods, ModListFormat.Plaintxt, All));
        Assert.Equal(ExportToModList.ToJson(Mods, All), ExportToModList.ExportModsToString(Mods, ModListFormat.Json, All));
        Assert.Equal(ExportToModList.ToCsv(Mods, All), ExportToModList.ExportModsToString(Mods, ModListFormat.Csv, All));
        Assert.Equal("unknown format:5", ExportToModList.ExportModsToString(Mods, ModListFormat.Custom, All));
    }

    [Fact]
    public void CustomTemplate_ReplacesAllPlaceholders()
    {
        var expected = "jei v11.6.0.1016 by mezz [238222] https://modrinth.com/mod/jei " +
                       "jei-1.20.1-forge-11.6.0.1016.jar\n" +
                       "JEI <addons> & \"stuff\" v1.2.3 by author one, author two [abc]  addons.jar";

        Assert.Equal(expected, ExportToModList.ExportModsToString(Mods,
            "{name} v{version} by {authors} [{mod_id}] {url} {filename}"));
    }

    [Fact]
    public void EffectiveVersion_FallsBackToMetadata()
    {
        Assert.Equal("11.6.0.1016", Jei.EffectiveVersion);
        Assert.Equal("1.2.3", Addons.EffectiveVersion);
    }

    [Fact]
    public void MarkdownEscaping_EscapesBackslashFirst()
    {
        Assert.Equal(@"a\-b", ExportToModList.ToMarkdownEscaped("a-b"));
        // the introduced backslashes are not re-escaped; a literal backslash is doubled
        Assert.Equal(@"a\\\-b", ExportToModList.ToMarkdownEscaped(@"a\-b"));
    }

    [Fact]
    public void HtmlEscaping_MatchesQt()
    {
        // Qt escapes only & < > and " (not ')
        Assert.Equal("&lt;script&gt; &amp; &quot;x&quot; 'y'",
            ExportToModList.ToHtmlEscaped("<script> & \"x\" 'y'"));
    }
}
