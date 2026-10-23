// SPDX-License-Identifier: GPL-3.0-only
// Byte-level compatibility tests for the INI layer against golden files produced by real
// QSettings (Qt 6.11) — see FlintLauncher/tools/qt-ini-golden. The *_dump.tsv fixtures record
// what QSettings itself reads back from the same files, so the parser is compared against Qt's
// own interpretation. READ side only (Flint never writes an INI — the writer
// half and its byte-level round-trip tests retired with the instance.cfg write path).

using FlintLauncher.Core.Settings;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class IniFileGoldenTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "IniFile", name);

    private static string TsvEscape(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\t", "<TAB>")
        .Replace("\n", "<NL>")
        .Replace("\r", "<CR>");

    private static string ExpectedRepr(string typeName, IniValue value) => typeName switch
    {
        "invalid" => "@INVALID",
        "QByteArray" => "BA:[" + Convert.ToBase64String(value.Bytes) + "]",
        "QStringList" => "STRLIST:[" + TsvEscape(string.Join(",", value.List)) + "]",
        _ => "STR:" + TsvEscape(value.String),
    };

    /// <summary>Our parser must decode exactly what QSettings decodes, for every key of the golden files.</summary>
    [Theory]
    [InlineData("golden_qsettings.ini", "golden_qsettings_dump.tsv")]
    [InlineData("torture_qsettings.ini", "torture_qsettings_dump.tsv")]
    public void Reader_Matches_QSettings_Dump(string iniFixture, string tsvFixture)
    {
        // The codec layer is the QSettings equivalent (IniFile adds Prism's ConfigVersion
        // migration on top; the golden fixtures have no ConfigVersion).
        var map = QSettingsIniCodec.Parse(File.ReadAllText(Fixture(iniFixture)));

        var expected = File.ReadAllLines(Fixture(tsvFixture));
        Assert.NotEmpty(expected);

        var checkedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in expected)
        {
            var parts = line.Split('\t');
            Assert.Equal(3, parts.Length);
            var (key, typeName, repr) = (parts[0], parts[1], parts[2]);
            if (typeName == "QVariantList")
            {
                // QDataStream-backed @Variant blobs: kept verbatim instead of decoded (the
                // launch path never consumes them, so there is no decode to verify)
                continue;
            }

            checkedKeys.Add(key);

            var value = map.GetValueOrDefault(key);
            Assert.True(value is not null, $"key {key} missing from our parse");

            if (value.ValueKind == IniValue.Kind.Raw)
            {
                continue; // opaque @-type; see above
            }

            Assert.Equal(repr, ExpectedRepr(typeName, value));
        }

        // our parse must not contain extra keys either
        Assert.Equal(checkedKeys.Count, expected.Length - expected.Count(l => l.Split('\t')[1] == "QVariantList"));
    }

    /// <summary>Port of IniFileTest::test_SaveAlreadyExistingFile (pre-1.1 legacy format) —
    /// the parse/migrate half only (Flint never re-saves an INI).</summary>
    [Fact]
    public void Legacy_NoConfigVersion_File_Parses()
    {
        var fileContent = "InstanceType=OneSix\n" +
                          "iconKey=vanillia_icon\n" +
                          "name=Minecraft Vanillia\n" +
                          "OverrideCommands=true\n" +
                          "PreLaunchCommand=\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link\n" +
                          "Wrapperommand=" + "\"\\\"$INST_JAVA\\\" -jar packwiz-installer-bootstrap.jar link =\"" + "\n";

        var path = Path.Combine(Path.GetTempPath(), $"flint-ini-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllText(path, fileContent);

            var f1 = new IniFile();
            Assert.True(f1.LoadFile(new AbsolutePath(path)));
            Assert.Equal("\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link", f1.GetString("PreLaunchCommand", "NOT SET"));
            Assert.Equal("\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link =", f1.GetString("Wrapperommand", "NOT SET"));
            Assert.Equal("1.3", f1.GetString("ConfigVersion", "NOT SET"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Port of IniFileTest::test_SaveAlreadyExistingFileWithSpecialCharsV1 (1.1 migration).</summary>
    [Fact]
    public void Legacy_V11_File_Migrates()
    {
        var fileContent = "InstanceType=OneSix\n" +
                          "ConfigVersion=1.1\n" +
                          "iconKey=vanillia_icon\n" +
                          "name=Minecraft Vanillia\n" +
                          "OverrideCommands=true\n" +
                          "PreLaunchCommand=" + "\"\\\"env mesa=true\\\"\"" + "\n";

        var path = Path.Combine(Path.GetTempPath(), $"flint-ini-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllText(path, fileContent);

            var f1 = new IniFile();
            Assert.True(f1.LoadFile(new AbsolutePath(path)));
            Assert.Equal("env mesa=true", f1.GetString("PreLaunchCommand", "NOT SET"));
            Assert.Equal("1.3", f1.GetString("ConfigVersion", "NOT SET"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
