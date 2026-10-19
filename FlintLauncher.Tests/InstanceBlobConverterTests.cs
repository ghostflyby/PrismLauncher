// SPDX-License-Identifier: GPL-3.0-only
// The three former "opaque JSON-text blob" config members (InstanceBlobConverters): the
// dual-form READ compatibility (the legacy wire form was a string holding the embedded
// JSON, the modern form is the native collection) and the free native WRITE shape, per
// docs/JSON-MIGRATION.md §1 (读兼容，不保证写兼容). The FromObject entry points cover the
// three runtime value shapes the settings registry feeds them (runtime Set / settings PUT
// JsonElement / legacy imported string).
//
// The wire documents are assembled through JsonNode where a real path must be injected:
// the legacy form is a STRING token holding the embedded JSON, and writing that by hand
// means escaping every quote of the blob into the outer document.

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Web;
using Xunit;

namespace FlintLauncher.Tests;

public class InstanceBlobConverterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-blob-{Guid.NewGuid():N}");

    public InstanceBlobConverterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string RealFilePath() => Path.Combine(_dir, "real-file.txt");

    private string RealDirPath() => Path.Combine(_dir, "real-dir");

    private static InstanceConfigFile Parse(string json) =>
        JsonSerializer.Deserialize(json, FlintJsonContext.Default.InstanceConfigFile)!;

    /// <summary>The legacy wire shape: <paramref name="member"/> holds the embedded JSON
    /// as a string token (what the C++ QJsonDocument blob looked like on disk).</summary>
    private static string LegacyDoc(string domain, string member, string blob) => new JsonObject
    {
        [domain] = new JsonObject { [member] = JsonValue.Create(blob) },
    }.ToJsonString();

    /// <summary>The modern wire shape: <paramref name="member"/> is the native collection.</summary>
    private static string NativeDoc(string domain, string member, JsonNode collection) => new JsonObject
    {
        [domain] = new JsonObject { [member] = collection },
    }.ToJsonString();

    // --------------------------------------------------------------------- Env (the map)

    [Fact]
    public void Env_Legacy_String_Blob_Parses_The_Embedded_Map()
    {
        var config = Parse(LegacyDoc("launch", "env", """{"JAVA_HOME":"/opt/jdk","X":"1"}"""));
        Assert.Equal("/opt/jdk", config.Launch.Env!["JAVA_HOME"]);
        Assert.Equal("1", config.Launch.Env!["X"]);
    }

    [Fact]
    public void Env_Native_Object_Binds_Directly()
    {
        var config = Parse(NativeDoc("launch", "env",
            JsonNode.Parse("""{"JAVA_HOME":"/opt/jdk21"}""")!));
        var env = Assert.IsType<EnvMap>(config.Launch.Env);
        Assert.Equal("/opt/jdk21", env["JAVA_HOME"]);
    }

    [Fact]
    public void Env_Junk_String_And_Wrong_Kinds_Collapse_To_An_Empty_Map()
    {
        // the former lenient read: anything unparseable / not an object -> an empty map
        Assert.Empty(Parse(LegacyDoc("launch", "env", "not json at all")).Launch.Env!);
        Assert.Empty(Parse(LegacyDoc("launch", "env", "[1,2]")).Launch.Env!); // array, not a map
        Assert.Empty(Parse("""{"launch":{"env":42}}""").Launch.Env!);        // wrong kind
    }

    [Fact]
    public void Env_Non_String_Values_Coerce_To_The_Empty_String()
    {
        // the C++ QVariantMap read was string-typed; a stray number lands as ""
        var config = Parse(NativeDoc("launch", "env", JsonNode.Parse("""{"K":7,"OK":"v"}""")!));
        Assert.Equal("", config.Launch.Env!["K"]);
        Assert.Equal("v", config.Launch.Env!["OK"]);
    }

    [Fact]
    public void Env_Write_Emits_The_Native_Object_And_Round_Trips()
    {
        var config = Parse(LegacyDoc("launch", "env", """{"A":"1"}"""));
        var written = JsonSerializer.Serialize(config, FlintJsonContext.Default.InstanceConfigFile);

        // the write shape is the native object, never the legacy string
        using var doc = JsonDocument.Parse(written);
        var env = doc.RootElement.GetProperty("launch").GetProperty("env");
        Assert.Equal(JsonValueKind.Object, env.ValueKind);
        Assert.Equal("1", env.GetProperty("A").GetString());

        Assert.Equal("1", Parse(written).Launch.Env!["A"]);
    }

    [Fact]
    public void Env_FromObject_Accepts_The_Three_Runtime_Value_Shapes()
    {
        Assert.Equal("runtime", EnvMapJsonConverter.FromObject(new EnvMap { ["K"] = "runtime" })!["K"]);

        var element = JsonDocument.Parse("""{"A":"element"}""").RootElement;
        Assert.Equal("element", EnvMapJsonConverter.FromObject(element.Clone())!["A"]);

        // the legacy embedded-JSON text, as a raw string and inside a string token
        Assert.Equal("legacy", EnvMapJsonConverter.FromObject("""{"B":"legacy"}""")!["B"]);
        var legacyToken = JsonDocument.Parse(""""{"B":"legacy"}"""").RootElement;
        Assert.Equal("legacy", EnvMapJsonConverter.FromObject(legacyToken.Clone())!["B"]);

        Assert.Null(EnvMapJsonConverter.FromObject(null));
        Assert.Null(EnvMapJsonConverter.FromObject(42));
    }

    // ----------------------------------------------------- Shortcuts (the filtered list)

    [Fact]
    public void Shortcuts_Legacy_String_Blob_Parses_The_Embedded_Array()
    {
        File.WriteAllText(RealFilePath(), "x");
        var blob = $$"""[{"name":"Minecraft","filePath":"{{RealFilePath()}}","target":1}]""";
        var config = Parse(LegacyDoc("identity", "shortcuts", blob));

        var shortcut = Assert.Single(config.Identity.Shortcuts!);
        Assert.Equal("Minecraft", shortcut.Name);
        Assert.Equal(RealFilePath(), shortcut.FilePath);
        Assert.Equal(ShortcutTarget.Applications, shortcut.Target);
    }

    [Fact]
    public void Shortcuts_Native_Array_Binds_Directly()
    {
        File.WriteAllText(RealFilePath(), "x");
        var config = Parse(NativeDoc("identity", "shortcuts",
            JsonNode.Parse($$"""[{"name":"N","filePath":"{{RealFilePath()}}","target":0}]""")!));
        var shortcut = Assert.Single(config.Identity.Shortcuts!);
        Assert.Equal(ShortcutTarget.Desktop, shortcut.Target);
    }

    [Fact]
    public void Shortcuts_Drops_Invalid_Entries_And_Nonexistent_Paths()
    {
        File.WriteAllText(RealFilePath(), "x");
        Directory.CreateDirectory(RealDirPath());
        var file = RealFilePath();
        var ghost = Path.Combine(_dir, "ghost");
        var array = JsonNode.Parse($$"""
            [
              {"name":"Good","filePath":"{{file}}","target":0},
              {"name":"DirEntry","filePath":"{{RealDirPath()}}","target":2},
              {"filePath":"{{file}}","target":0},
              {"name":"NoTarget","filePath":"{{file}}"},
              {"name":"BadTarget","filePath":"{{file}}","target":3},
              {"name":"NegTarget","filePath":"{{file}}","target":-1},
              {"name":"Ghost","filePath":"{{ghost}}","target":0},
              "not-a-shortcut",
              {"name":42,"filePath":"{{file}}","target":0}
            ]
            """)!;
        var config = Parse(NativeDoc("identity", "shortcuts", array));

        // only the well-formed entries whose path exists survive the former reader's filter
        var kept = config.Identity.Shortcuts!;
        Assert.Equal(2, kept.Count);
        Assert.Equal("Good", kept[0].Name);
        Assert.Equal(ShortcutTarget.Desktop, kept[0].Target);
        Assert.Equal("DirEntry", kept[1].Name); // a directory passes the existence filter too
        Assert.Equal(ShortcutTarget.Other, kept[1].Target);
    }

    [Fact]
    public void Shortcuts_Write_Emits_The_Native_Array()
    {
        File.WriteAllText(RealFilePath(), "x");
        var config = new InstanceConfigFile
        {
            Identity = new InstanceIdentityConfig
            {
                Shortcuts = [new ShortcutData("Game", RealFilePath(), ShortcutTarget.Other)],
            },
        };
        var written = JsonSerializer.Serialize(config, FlintJsonContext.Default.InstanceConfigFile);

        using var doc = JsonDocument.Parse(written);
        var arr = doc.RootElement.GetProperty("identity").GetProperty("shortcuts");
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal("Game", arr[0].GetProperty("name").GetString());
        Assert.Equal(RealFilePath(), arr[0].GetProperty("filePath").GetString());
        Assert.Equal(2, arr[0].GetProperty("target").GetInt32());

        Assert.Equal(ShortcutTarget.Other, Assert.Single(Parse(written).Identity.Shortcuts!).Target);
    }

    [Fact]
    public void Shortcuts_Junk_And_Empty_Legacy_Text_Yields_No_List()
    {
        Assert.Null(Parse(LegacyDoc("identity", "shortcuts", "not json")).Identity.Shortcuts);
        Assert.Null(Parse(LegacyDoc("identity", "shortcuts", "")).Identity.Shortcuts);
        Assert.Null(Parse("""{"identity":{"shortcuts":42}}""").Identity.Shortcuts);
        // an embedded array whose entries all fail validation reads as an EMPTY list
        Assert.Empty(Parse(LegacyDoc("identity", "shortcuts", """["x"]""")).Identity.Shortcuts!);
    }

    // ------------------------------------------- ModDownloadLoaders (the string list)

    [Fact]
    public void ModDownloadLoaders_Legacy_String_Blob_Parses_The_Embedded_Array()
    {
        var config = Parse(LegacyDoc("misc", "modDownloadLoaders", """["forge","fabric"]"""));
        Assert.Equal(["forge", "fabric"], config.Misc.ModDownloadLoaders);
    }

    [Fact]
    public void ModDownloadLoaders_Native_Array_Binds_Directly_And_Skips_Non_Strings()
    {
        var config = Parse(NativeDoc("misc", "modDownloadLoaders", JsonNode.Parse("""["quilt",7,null]""")!));
        Assert.Equal(["quilt"], config.Misc.ModDownloadLoaders);
    }

    [Fact]
    public void ModDownloadLoaders_Junk_And_Wrong_Kinds_Yield_No_List()
    {
        Assert.Null(Parse(LegacyDoc("misc", "modDownloadLoaders", "oops")).Misc.ModDownloadLoaders);
        Assert.Null(Parse("""{"misc":{"modDownloadLoaders":3}}""").Misc.ModDownloadLoaders);
        Assert.Null(Parse(LegacyDoc("misc", "modDownloadLoaders", "")).Misc.ModDownloadLoaders);
    }

    [Fact]
    public void ModDownloadLoaders_Write_Emits_The_Native_Array()
    {
        var config = new InstanceConfigFile
        {
            Misc = new InstanceMiscConfig(modDownloadLoaders: ["forge"]),
        };
        var written = JsonSerializer.Serialize(config, FlintJsonContext.Default.InstanceConfigFile);

        using var doc = JsonDocument.Parse(written);
        var arr = doc.RootElement.GetProperty("misc").GetProperty("modDownloadLoaders");
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal("forge", arr[0].GetString());
    }

    [Fact]
    public void ModDownloadLoaders_FromObject_Accepts_The_Three_Runtime_Value_Shapes()
    {
        Assert.Equal(["a"], StringListJsonConverter.FromObject(new List<string> { "a" }));
        var element = JsonDocument.Parse("""["b"]""").RootElement;
        Assert.Equal(["b"], StringListJsonConverter.FromObject(element.Clone()));
        Assert.Equal(["c"], StringListJsonConverter.FromObject("""["c"]"""));
        var legacyToken = JsonDocument.Parse(""""["c"]"""").RootElement;
        Assert.Equal(["c"], StringListJsonConverter.FromObject(legacyToken.Clone()));
        Assert.Null(StringListJsonConverter.FromObject(null));
        Assert.Null(StringListJsonConverter.FromObject(1));
    }
}
