// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the GameOptions port (launcher/minecraft/gameoptions/GameOptions.{h,cpp}): the
// key:value parsing (first ':' separator, untrimmed raw values, "version" line extraction),
// the whole-file rewrite through a save file and the edit helpers.

using FlintLauncher.Core.Minecraft.GameOptions;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class GameOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-gameoptions-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private AbsolutePath OptionsPath(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "options.txt");
        File.WriteAllText(path, content);
        return new AbsolutePath(path);
    }

    // ---------------------------------------------------------------- load

    [Fact]
    public void Load_Parses_Key_Value_Lines()
    {
        var path = OptionsPath("version:3583\nlang:en_us\nfov:0.0\nmusic:0.53212\n");
        var options = new GameOptions(path);

        Assert.True(options.IsLoaded);
        Assert.Equal(3583, options.Version);
        Assert.Equal(3, options.RowCount());
        Assert.Equal("lang", options.Contents[0].Key);
        Assert.Equal("en_us", options.Contents[0].Value);
        Assert.Equal("fov", options.Contents[1].Key);
        Assert.Equal("0.0", options.Contents[1].Value);
        Assert.Equal("music", options.Contents[2].Key);
        Assert.Equal("0.53212", options.Contents[2].Value);
    }

    // values are everything after the FIRST ':', taken verbatim (no trimming/escaping)
    [Fact]
    public void Load_Values_May_Contain_Colons_And_Are_Not_Trimmed()
    {
        var path = OptionsPath("key with:colon:value\nspaces:  padded value  \n");
        var options = new GameOptions(path);

        Assert.Equal("key with", options.Contents[0].Key);
        Assert.Equal("colon:value", options.Contents[0].Value);
        Assert.Equal("spaces", options.Contents[1].Key);
        Assert.Equal("  padded value  ", options.Contents[1].Value);
    }

    [Fact]
    public void Load_Skips_Lines_Without_A_Colon()
    {
        var path = OptionsPath("\nno colon here\nvalid:1\n");
        var options = new GameOptions(path);

        Assert.Single(options.Contents);
        Assert.Equal("valid", options.Contents[0].Key);
    }

    // QFile::readLine() chops only the '\n'; a '\r' survives inside the value.
    [Fact]
    public void Load_Keeps_Carriage_Returns_In_Values()
    {
        var path = OptionsPath("lang:en_us\r\nmusic:1.0\r\n");
        var options = new GameOptions(path);

        Assert.Equal("en_us\r", options.Contents[0].Value);
        Assert.Equal("1.0\r", options.Contents[1].Value);
    }

    [Fact]
    public void Version_Line_Is_Not_An_Item_And_Parses_Like_QString_ToInt()
    {
        var path = OptionsPath("version:1234\nversioned:keep\n");
        var options = new GameOptions(path);

        // only the exact "version" key is pulled out
        Assert.Equal(1234, options.Version);
        Assert.DoesNotContain(options.Contents, i => i.Key == "version");
        Assert.Equal("keep", Assert.Single(options.Contents, i => i.Key == "versioned").Value);

        // a non-numeric version parses to 0 (QString::toInt failure), like the C++
        var bad = new GameOptions(OptionsPath("version:notanumber\na:b\n"));
        Assert.Equal(0, bad.Version);
        Assert.DoesNotContain(bad.Contents, i => i.Key == "version");

        // leading whitespace + sign are accepted; trailing whitespace is not
        Assert.Equal(-5, new GameOptions(OptionsPath("version: -5\n")).Version);
        Assert.Equal(0, new GameOptions(OptionsPath("version:5 \n")).Version);
    }

    [Fact]
    public void Missing_File_Is_Not_Loaded()
    {
        var options = new GameOptions(new AbsolutePath(Path.Combine(_dir, "missing", "options.txt")));
        Assert.False(options.IsLoaded);
        Assert.Empty(options.Contents);
        Assert.Equal(0, options.Version);
    }

    // ---------------------------------------------------------------- save / round-trip

    [Fact]
    public void Save_Rewrites_The_File_In_The_Cpp_Layout()
    {
        var path = OptionsPath("version:3583\nlang:en_us\nmusic:0.53212\n");
        var options = new GameOptions(path);
        Assert.True(options.Save());

        // the "version:" line comes FIRST (when non-zero), then every item as key:value\n
        Assert.Equal("version:3583\nlang:en_us\nmusic:0.53212\n", path.ReadAllText());
    }

    [Fact]
    public void Save_Omits_The_Version_Line_When_Zero()
    {
        var path = OptionsPath("a:1\n");
        var options = new GameOptions(path);
        Assert.True(options.Save());
        Assert.Equal("a:1\n", path.ReadAllText());
    }

    [Fact]
    public void RoundTrip_Preserves_Values_With_Colons()
    {
        var path = OptionsPath("resourcePacks:[\"vanilla\",\"weird:name\"]\n");
        var options = new GameOptions(path);
        Assert.Equal("[\"vanilla\",\"weird:name\"]", options.Contents[0].Value);
        Assert.True(options.Save());

        var reloaded = new GameOptions(path);
        Assert.Equal(options.Contents, reloaded.Contents);
    }

    [Fact]
    public void Save_Creates_A_Missing_File()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "new", "options.txt");
        var pathDir = Path.GetDirectoryName(path);
        Assert.NotNull(pathDir);
        Directory.CreateDirectory(pathDir);
        var options = new GameOptions(new AbsolutePath(path));
        Assert.False(options.IsLoaded);
        Assert.True(options.Save());
        Assert.Equal("", File.ReadAllText(path));
    }

    [Fact]
    public void Save_Keeps_Prior_Contents_On_Unmodified_Reload()
    {
        var original = "version:3465\nsneakToggle:true\nlang:de_de\n";
        var path = OptionsPath(original);
        var options = new GameOptions(path);
        Assert.True(options.Save());
        Assert.Equal(original, path.ReadAllText());
    }

    // ---------------------------------------------------------------- edit helpers

    [Fact]
    public void Set_Updates_Existing_Or_Appends()
    {
        var path = OptionsPath("lang:en_us\n");
        var options = new GameOptions(path);

        options.Set("lang", "de_de");
        var lang = options.Find("lang");
        Assert.NotNull(lang);
        Assert.Equal("de_de", lang.Value);
        Assert.Single(options.Contents);

        options.Set("fov", "0.75");
        Assert.Equal(2, options.RowCount());
        Assert.True(options.Save());
        Assert.Equal("lang:de_de\nfov:0.75\n", path.ReadAllText());
    }

    [Fact]
    public void Remove_Deletes_All_Matching_Items()
    {
        var path = OptionsPath("lang:en_us\nmusic:1.0\n");
        var options = new GameOptions(path);

        Assert.True(options.Remove("lang"));
        Assert.False(options.Remove("lang"));
        Assert.Single(options.Contents);
        Assert.True(options.Save());
        Assert.Equal("music:1.0\n", path.ReadAllText());
    }

    [Fact]
    public void Reload_ReReads_The_File()
    {
        var path = OptionsPath("a:1\n");
        var options = new GameOptions(path);
        path.WriteAllText("version:9\nb:2\n");
        Assert.True(options.Reload());
        Assert.Equal(9, options.Version);
        Assert.Equal("b", Assert.Single(options.Contents).Key);
    }

    [Fact]
    public void Find_Returns_Null_For_Unknown_Keys()
    {
        var options = new GameOptions(OptionsPath("a:1\n"));
        Assert.Null(options.Find("zzz"));
        Assert.NotNull(options.Find("a"));
    }
}
