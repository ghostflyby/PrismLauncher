// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Fix-1 tests for the LocalModParseTask port (Minecraft/Resources/LocalModParse.cs): the
// jar/folder metadata readers (fabric.mod.json, quilt.mod.json, META-INF/mods.toml +
// neoforge.mods.toml, mcmod.info, forgeversion.properties, litemod.json, nilmod.css) and
// the ModFolderModel.Update() details-population pass. Synthetic jars are built in-test
// with System.IO.Compression (entry order matters for the forge jarVersion/MANIFEST dance
// and the nilloader canary pair).

using System.IO.Compression;
using System.Text;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class LocalModParseTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("flint_mod_parse_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { }
    }

    private string TempFile(string name) => Path.Combine(_tempRoot, name);

    private static void WriteJar(string path, params (string Name, string Content)[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    private static Mod Parsed(string path)
    {
        var mod = new Mod(new AbsolutePath(path));
        ModUtils.Process(mod);
        return mod;
    }

    // ------------------------------------------------------------------ fabric

    public static string FabricJson => """
                                       {
                                           "schemaVersion": 1,
                                           "id": "testmod",
                                           "version": "1.2.3",
                                           "name": "Test Mod",
                                           "description": "A fabric test mod",
                                           "authors": [ { "name": "alice", "url": "https://alice.example" }, "bob" ],
                                           "contact": { "homepage": "https://example.org", "issues": "https://example.org/issues" },
                                           "license": [ "MIT", { "name": "Apache", "id": "Apache-2.0", "url": "https://spdx.org/licenses/Apache-2.0.html" } ],
                                           "icon": { "32x32": "icons/small.png", "64x64": "icons/big.png" },
                                           "depends": { "fabricloader": ">=0.14", "minecraft": "1.20", "fabric-api": "*", "other-mod": "*" }
                                       }
                                       """;

    [Fact]
    public void Fabric_FullManifest_ParsesAllFields()
    {
        var jar = TempFile("test.jar");
        WriteJar(jar, ("fabric.mod.json", FabricJson));

        var mod = Parsed(jar);

        Assert.True(mod.Valid);
        Assert.Equal("testmod", mod.Details.ModId);
        Assert.Equal("1.2.3", mod.Details.Version);
        Assert.Equal("Test Mod", mod.Details.Name);
        Assert.Equal("A fabric test mod", mod.Details.Description);
        Assert.Equal(["alice", "bob"], mod.Details.Authors);
        Assert.Equal("https://example.org", mod.Details.Homeurl);
        Assert.Equal("https://example.org/issues", mod.Details.IssueTracker);
        Assert.Equal(2, mod.Details.Licenses.Count);
        Assert.Equal("MIT", mod.Details.Licenses[0].Id);
        Assert.Equal("Apache-2.0", mod.Details.Licenses[1].Id);
        // the largest icon wins; only the inner path is recorded (no extraction)
        Assert.Equal("icons/big.png", mod.Details.IconFile);
        // fabricloader/minecraft/fabric-* keys are filtered out like the C++
        Assert.Equal(["other-mod"], mod.Details.Dependencies);
    }

    [Fact]
    public void Fabric_NameFallsBackToModId_AndStringIcon()
    {
        var jar = TempFile("fallback.jar");
        WriteJar(jar, ("fabric.mod.json", """
                                          { "schemaVersion": 1, "id": "plainmod", "version": "0.1", "icon": "icon.png" }
                                          """));

        var mod = Parsed(jar);

        Assert.Equal("plainmod", mod.Details.Name);
        Assert.Equal("icon.png", mod.Details.IconFile);
    }

    [Fact]
    public void Fabric_SchemaVersionZero_SkipsTheOptionalBlock()
    {
        var jar = TempFile("noschema.jar");
        WriteJar(jar, ("fabric.mod.json", """{ "id": "oldschool", "version": "0.0.1" }"""));

        var mod = Parsed(jar);

        Assert.Equal("oldschool", mod.Details.ModId);
        Assert.Empty(mod.Details.Authors);
        Assert.Empty(mod.Details.Licenses);
    }

    [Fact]
    public void Fabric_GarbageJson_IsBestEffortEmpty()
    {
        var jar = TempFile("garbage.jar");
        WriteJar(jar, ("fabric.mod.json", "{{{ not json"));

        var mod = new Mod(new AbsolutePath(jar));

        // the parse "succeeds" (a metadata candidate was found) but yields no details
        Assert.True(ModUtils.Process(mod));
        Assert.Equal("", mod.Details.ModId);
        Assert.False(mod.Valid);
    }

    // ------------------------------------------------------------------ quilt

    [Fact]
    public void Quilt_Metadata_Parses()
    {
        var jar = TempFile("quilt.jar");
        WriteJar(jar, ("quilt.mod.json", """
                                         {
                                             "schema_version": 1,
                                             "quilt_loader": {
                                                 "id": "quiltmod",
                                                 "version": "2.0.0",
                                                 "metadata": {
                                                     "name": "Quilt Mod",
                                                     "description": "A quilt mod",
                                                     "contributors": { "alice": "Author", "bob": "Contributor" },
                                                     "contact": { "homepage": "https://quilt.example", "issues": "https://quilt.example/issues" },
                                                     "license": "MIT",
                                                     "icon": { "32x32": "assets/small.png", "64x64": "assets/big.png" }
                                                 }
                                             },
                                             "depends": [ "minecraft", "quilt_loader", "plain_dep", { "id": "opt_dep", "optional": true }, { "id": "hard_dep" } ]
                                         }
                                         """));

        var mod = Parsed(jar);

        Assert.Equal("quiltmod", mod.Details.ModId);
        Assert.Equal("2.0.0", mod.Details.Version);
        Assert.Equal("Quilt Mod", mod.Details.Name);
        Assert.Equal("A quilt mod", mod.Details.Description);
        Assert.Equal(["alice", "bob"], mod.Details.Authors);
        Assert.Equal("https://quilt.example", mod.Details.Homeurl);
        Assert.Equal("https://quilt.example/issues", mod.Details.IssueTracker);
        Assert.Equal("MIT", mod.Details.Licenses[0].Id);
        Assert.Equal("assets/big.png", mod.Details.IconFile);
        // minecraft/quilt_* filtered; optional entries skipped (the depends block sits on the
        // root object, exactly like the C++ reader reads it)
        Assert.Equal(["plain_dep", "hard_dep"], mod.Details.Dependencies);
    }

    [Fact]
    public void Quilt_UnsupportedSchema_YieldsEmptyDetails()
    {
        var jar = TempFile("quilt-old.jar");
        WriteJar(jar, ("quilt.mod.json", """{ "schema_version": 2, "quilt_loader": { "id": "nope" } }"""));

        var mod = Parsed(jar);

        Assert.Equal("", mod.Details.ModId);
    }

    // ------------------------------------------------------------------ forge TOML

    [Fact]
    public void ForgeModsToml_NewFormat_Parses()
    {
        var jar = TempFile("forge.jar");
        WriteJar(jar, ("META-INF/mods.toml", """
                                             modLoader = "javafml"
                                             loaderVersion = "[41,)"
                                             license = "LGPL-3.0"
                                             issueTrackerURL = "https://github.com/example/issue"
                                             authors = "The Forge Team"
                                             displayURL = "example.org"

                                             [[mods]]
                                             modId = "forgemod"
                                             version = "3.2.1"
                                             displayName = "Forge Mod"
                                             description = "A forge mod"
                                             logoFile = "logo.png"

                                             [[dependencies.forgemod]]
                                             modId = "forge"
                                             mandatory = true

                                             [[dependencies.forgemod]]
                                             modId = "minecraft"
                                             mandatory = true

                                             [[dependencies.forgemod]]
                                             modId = "required_lib"
                                             mandatory = true

                                             [[dependencies.forgemod]]
                                             modId = "optional_lib"
                                             mandatory = false
                                             """));

        var mod = Parsed(jar);

        Assert.Equal("forgemod", mod.Details.ModId);
        Assert.Equal("3.2.1", mod.Details.Version);
        Assert.Equal("Forge Mod", mod.Details.Name);
        Assert.Equal("A forge mod", mod.Details.Description);
        Assert.Equal(["The Forge Team"], mod.Details.Authors);
        // the displayURL fix-up prepends http://
        Assert.Equal("http://example.org", mod.Details.Homeurl);
        Assert.Equal("https://github.com/example/issue", mod.Details.IssueTracker);
        Assert.Equal("LGPL-3.0", mod.Details.Licenses[0].Id);
        Assert.Equal("logo.png", mod.Details.IconFile);
        // forge/minecraft ignored; only mandatory deps collected
        Assert.Equal(["required_lib"], mod.Details.Dependencies);
    }

    [Fact]
    public void NeoForgeModsToml_TypeRequired_Parses()
    {
        var jar = TempFile("neoforge.jar");
        WriteJar(jar, ("META-INF/neoforge.mods.toml", """
                                                      [[mods]]
                                                      modId = "neomod"
                                                      version = "1.0.0"
                                                      displayName = "Neo Mod"

                                                      [[dependencies.neomod]]
                                                      modId = "neoforge"
                                                      type = "required"

                                                      [[dependencies.neomod]]
                                                      modId = "hard_lib"
                                                      type = "required"

                                                      [[dependencies.neomod]]
                                                      modId = "soft_lib"
                                                      type = "optional"
                                                      """));

        var mod = Parsed(jar);

        Assert.Equal("neomod", mod.Details.ModId);
        Assert.Equal("Neo Mod", mod.Details.Name);
        // neoforge itself is ignored; only type="required" deps are collected
        Assert.Equal(["hard_lib"], mod.Details.Dependencies);
    }

    [Fact]
    public void ForgeModsToml_JarVersionPlaceholder_FallsBackToManifest()
    {
        // the mods.toml entry comes first; the version placeholder is only resolved once the
        // MANIFEST.MF entry is reached
        var jar = TempFile("jarversion.jar");
        WriteJar(jar,
            ("META-INF/mods.toml", """
                                   [[mods]]
                                   modId = "placeholder"
                                   version = "${file.jarVersion}"
                                   displayName = "Placeholder Mod"
                                   """),
            ("some/class/file.class", "irrelevant"),
            ("META-INF/MANIFEST.MF", """
                                     Manifest-Version: 1.0
                                     Implementation-Version: 9.9.9
                                     """));

        var mod = Parsed(jar);

        Assert.Equal("9.9.9", mod.Details.Version);
        Assert.Equal("Placeholder Mod", mod.Details.Name);
    }

    [Fact]
    public void ForgeModsToml_BrokenGradleVersion_BecomesNone()
    {
        var jar = TempFile("gradle-mess.jar");
        WriteJar(jar,
            ("META-INF/mods.toml", """
                                   [[mods]]
                                   modId = "messy"
                                   version = "${file.jarVersion}"
                                   """),
            ("META-INF/MANIFEST.MF", "Implementation-Version: task ':jar' property 'archiveVersion'.\n"));

        var mod = Parsed(jar);

        Assert.Equal("NONE", mod.Details.Version);
    }

    [Fact]
    public void ForgeModsToml_MissingModsArray_YieldsEmptyDetails()
    {
        var jar = TempFile("corrupt-toml.jar");
        WriteJar(jar, ("META-INF/mods.toml", "modLoader = \"javafml\"\n"));

        var mod = Parsed(jar);

        Assert.Equal("", mod.Details.ModId);
    }

    // ------------------------------------------------------------------ mcmod.info

    [Fact]
    public void McModInfo_LegacyArray_Parses()
    {
        var jar = TempFile("legacy.jar");
        WriteJar(jar, ("mcmod.info", """
                                     [
                                         {
                                             "modid": "mod_legacy",
                                             "name": "Legacy Mod",
                                             "version": "1.7.10",
                                             "url": "legacy.example.org",
                                             "description": "An old mod",
                                             "authorList": [ "grandpa" ],
                                             "logoFile": "assets/logo.png",
                                             "requiredMods": [ "mod_MinecraftForge", "custom:1.2@mc1.7.10" ]
                                         }
                                     ]
                                     """));

        var mod = Parsed(jar);

        Assert.Equal("legacy", mod.Details.ModId); // the mod_ prefix is stripped
        Assert.Equal("Legacy Mod", mod.Details.Name);
        Assert.Equal("1.7.10", mod.Details.Version);
        Assert.Equal("http://legacy.example.org", mod.Details.Homeurl); // the url fix-up
        Assert.Equal(["grandpa"], mod.Details.Authors);
        Assert.Equal("assets/logo.png", mod.Details.IconFile);
        // mod_MinecraftForge ignored; the version/annotation suffix dance of the C++
        // addDep leaves the bare version ("1.2@mc1.7.10" -> "1.2")
        Assert.Equal(["1.2"], mod.Details.Dependencies);
    }

    [Fact]
    public void McModInfo_ExampleModName_IsIgnored()
    {
        var jar = TempFile("example.jar");
        WriteJar(jar, ("mcmod.info", """
                                     [ { "modid": "copied", "name": "Example Mod", "version": "1.0" } ]
                                     """));

        var mod = Parsed(jar);

        Assert.Equal("", mod.Details.Name);
        Assert.Equal("copied", mod.Details.ModId);
    }

    [Fact]
    public void McModInfo_ObjectFormat_Parses()
    {
        var jar = TempFile("modlist.jar");
        WriteJar(jar, ("mcmod.info", """
                                     {
                                         "modListVersion": 2,
                                         "modList": [ { "modid": "objmod", "name": "Object Mod", "version": "2.0", "authors": [ "a", "b" ] } ]
                                     }
                                     """));

        var mod = Parsed(jar);

        Assert.Equal("objmod", mod.Details.ModId);
        Assert.Equal("Object Mod", mod.Details.Name);
        Assert.Equal(["a", "b"], mod.Details.Authors);
    }

    // ------------------------------------------------------------------ forge props / litemod

    [Fact]
    public void ForgeProperties_BuildsDottedVersion()
    {
        var jar = TempFile("forge-old.jar");
        WriteJar(jar, ("forgeversion.properties", """
                                                  forge.major.number = 41
                                                  forge.minor.number = 0
                                                  forge.revision.number = 98
                                                  forge.build.number = 2
                                                  """));

        var mod = Parsed(jar);

        Assert.Equal("Forge", mod.Details.ModId);
        Assert.Equal("Minecraft Forge", mod.Details.Name);
        Assert.Equal("41.0.98.2", mod.Details.Version);
        Assert.Equal("http://www.minecraftforge.net/forum/", mod.Details.Homeurl);
    }

    [Fact]
    public void Litemod_Parses()
    {
        var path = TempFile("liteloader-mod.litemod");
        WriteJar(path, ("litemod.json", """
                                        { "name": "litetest", "version": "1.4", "mcversion": "1.7.10", "author": "litemaster", "description": "lite" }
                                        """));

        var mod = Parsed(path);

        Assert.Equal(ResourceType.LiteMod, mod.Type);
        Assert.Equal("litetest", mod.Details.ModId);
        Assert.Equal("litetest", mod.Details.Name);
        Assert.Equal("1.4", mod.Details.Version);
        Assert.Equal("1.7.10", mod.Details.McVersion);
        Assert.Equal(["litemaster"], mod.Details.Authors);
    }

    // ------------------------------------------------------------------ nilloader (qdcss)

    [Fact]
    public void NilMod_CommentEmbeddedProperties_Parse()
    {
        var jar = TempFile("nil.jar");
        WriteJar(jar,
            ("META-INF/nil/mappings.json", "{}"),
            ("custom.nilmod.css", """
                                  /* NilLoader metadata */
                                  @nilmod {
                                      name: "Nil Test";
                                      description: "A nil mod";
                                      authors: "unascribed";
                                      version: "1.0.0";
                                  }
                                  .some-class { color: red; }
                                  """));

        var mod = Parsed(jar);

        Assert.True(mod.Valid);
        Assert.Equal("custom", mod.Details.ModId); // from the .nilmod.css filename
        Assert.Equal("Nil Test", mod.Details.Name);
        Assert.Equal("A nil mod", mod.Details.Description);
        Assert.Equal(["unascribed"], mod.Details.Authors);
        Assert.Equal("1.0.0", mod.Details.Version);
    }

    [Fact]
    public void NilMod_MissingVersion_Questions()
    {
        var jar = TempFile("nil2.jar");
        WriteJar(jar,
            ("META-INF/nil/mappings.json", "{}"),
            ("shaded.nilmod.css", "@nilmod { name: \"No Version\"; }"));

        var mod = Parsed(jar);

        Assert.Equal("?", mod.Details.Version);
        Assert.Equal("No Version", mod.Details.Name);
    }

    // ------------------------------------------------------------------ folders + validate

    [Fact]
    public void Folder_McModInfo_Parses()
    {
        var dir = Path.Combine(_tempRoot, "folder-mod");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mcmod.info"), """
                                                           [ { "modid": "foldermod", "name": "Folder Mod", "version": "0.5" } ]
                                                           """);

        var mod = Parsed(dir);

        Assert.Equal(ResourceType.Folder, mod.Type);
        Assert.Equal("foldermod", mod.Details.ModId);
        Assert.Equal("Folder Mod", mod.Details.Name);
    }

    [Fact]
    public void Folder_WithoutMcModInfo_Fails()
    {
        var dir = Path.Combine(_tempRoot, "plain-folder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "some.txt"), "hi");

        var mod = new Mod(new AbsolutePath(dir));

        Assert.False(ModUtils.Process(mod));
        Assert.Equal("", mod.Details.ModId);
    }

    [Fact]
    public void Validate_RejectsNonModJar()
    {
        var jar = TempFile("random.jar");
        WriteJar(jar, ("random.txt", "nothing to see here"));

        Assert.False(ModUtils.Validate(jar));
    }

    [Fact]
    public void Validate_AcceptsFabricJar()
    {
        var jar = TempFile("valid.jar");
        WriteJar(jar, ("fabric.mod.json", """{ "schemaVersion": 1, "id": "validmod", "version": "1" }"""));

        Assert.True(ModUtils.Validate(jar));
    }

    // ------------------------------------------------------------------ ModFolderModel wiring

    [Fact]
    public void ModFolderModel_Scan_PopulatesModDetails()
    {
        var modsDir = Path.Combine(_tempRoot, "mods");
        Directory.CreateDirectory(modsDir);
        WriteJar(Path.Combine(modsDir, "scanned.jar"), ("fabric.mod.json", FabricJson));
        // a folder mod with mcmod.info next to the jar
        var folderMod = Path.Combine(modsDir, "folder-mod");
        Directory.CreateDirectory(folderMod);
        File.WriteAllText(Path.Combine(folderMod, "mcmod.info"),
            """[ { "modid": "foldermod", "name": "Folder Mod", "version": "0.5" } ]""");

        var model = new ModFolderModel(MaterializedDirectory.Ensure(new AbsolutePath(modsDir))!, isIndexed: false);
        Assert.True(model.Update());

        Assert.Equal(2, model.Size);
        var byName = model.AllMods().ToDictionary(m => m.Fileinfo.Name);
        var scanned = byName["scanned.jar"];
        var folder = byName["folder-mod"];

        Assert.True(scanned.Valid);
        Assert.Equal("testmod", scanned.Details.ModId);
        Assert.Equal("Test Mod", scanned.Name); // the details name wins over the filename
        Assert.Equal("1.2.3", scanned.Version);
        Assert.Equal(["alice", "bob"], scanned.Authors());

        Assert.True(folder.Valid);
        Assert.Equal("foldermod", folder.Details.ModId);
        Assert.Equal("Folder Mod", folder.Name);

        // a second update must keep the resolved details (unchanged resources are not re-parsed)
        Assert.True(model.Update());
        var rescanned = model.AllMods().First(m => m.Fileinfo.Name == "scanned.jar");
        Assert.Equal("testmod", rescanned.Details.ModId);
    }
}
