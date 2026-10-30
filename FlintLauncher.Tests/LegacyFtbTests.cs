// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the legacy FTB platform: the modpacks.xml/thirdparty.xml parsing (attributes,
// oldVersions repair, bugged/broken flags), private pack fetching, the private pack code
// file round-trip and the end-to-end install of a CDN archive (unzip -> minecraft folder,
// forge from pack.json, jar mods from instMods, the no-method failure). Each task is
// constructed with its CdnBaseUrl pointed at the stub (per-instance, nothing
// process-global to restore).

using System.Net;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.LegacyFtb;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class LegacyFtbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-legacy-ftb-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubLegacyFtbServer _server = new();

    public LegacyFtbTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>A fetch task whose CDN points at the stub server.</summary>
    private LegacyFtbPackFetchTask CreateFetchTask() =>
        new() { CdnBaseUrl = _server.Url + "/", Http = TestNet.Http };

    private InstanceList CreateList()
    {
        var instancesDir = Path.Combine(_root, "instances");
        Directory.CreateDirectory(instancesDir);
        return _global.BareInstanceList(instancesDir);
    }

    private static List<(string? Uid, string? Version)> Components(string staging)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "mmc-pack.json")));
        return doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(),
                Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
            .ToList();
    }

    // --------------------------------------------------------------------- fetch/xml

    [Fact]
    public async Task Fetch_ParsesPublicAndThirdPartyLists()
    {
        _server.ModpacksXml = """
                              <?xml version="1.0" encoding="UTF-8"?>
                              <modpacks>
                                  <modpack name="Public Pack" version="1.2.3" mcVersion="1.7.10"
                                           description="a public pack" mods="42" logo="public.png"
                                           oldVersions="1.2.2;1.2.1" author="alice" dir="PublicPack" url="PublicPack-1.2.3.zip"/>
                              </modpacks>
                              """;
        _server.ThirdPartyXml = """
                                <?xml version="1.0" encoding="UTF-8"?>
                                <modpacks>
                                    <modpack name="Third Party Pack" version="3.1" mcVersion="1.12.2"
                                             description="" mods="" logo="" oldVersions="" author="bob"
                                             dir="ThirdPartyPack" url="ThirdPartyPack-3.1.zip"/>
                                </modpacks>
                                """;

        var task = CreateFetchTask();
        await task.FetchAsync(CancellationToken.None);

        var publicPack = Assert.Single(task.PublicPacks);
        Assert.Equal(PackType.Public, publicPack.Type);
        Assert.Equal("Public Pack", publicPack.Name);
        Assert.Equal("1.2.3", publicPack.CurrentVersion);
        Assert.Equal("1.7.10", publicPack.McVersion);
        Assert.Equal("a public pack", publicPack.Description);
        Assert.Equal("42", publicPack.Mods);
        Assert.Equal("public.png", publicPack.Logo);
        Assert.Equal(new[] { "1.2.2", "1.2.1" }, publicPack.OldVersions);
        Assert.Equal("alice", publicPack.Author);
        Assert.Equal("PublicPack", publicPack.Dir);
        Assert.Equal("PublicPack-1.2.3.zip", publicPack.File);
        Assert.False(publicPack.Bugged);
        Assert.False(publicPack.Broken);

        var thirdPartyPack = Assert.Single(task.ThirdPartyPacks);
        Assert.Equal(PackType.ThirdParty, thirdPartyPack.Type);
        // the empty oldVersions attribute is repaired from the current version
        Assert.DoesNotContain(thirdPartyPack.OldVersions, v => v.Length == 0);
        Assert.Equal(new[] { "3.1" }, thirdPartyPack.OldVersions);
        Assert.True(thirdPartyPack.Bugged);
        Assert.False(thirdPartyPack.Broken);
    }

    [Fact]
    public async Task Fetch_PackWithoutAnyVersion_IsBroken()
    {
        _server.ModpacksXml = """
                              <?xml version="1.0" encoding="UTF-8"?>
                              <modpacks>
                                  <modpack name="Broken Pack" version="" mcVersion="1.7.10" oldVersions="" dir="Broken"/>
                              </modpacks>
                              """;
        _server.ThirdPartyXml = """<modpacks></modpacks>""";

        var task = CreateFetchTask();
        await task.FetchAsync(CancellationToken.None);

        var pack = Assert.Single(task.PublicPacks);
        Assert.True(pack.Bugged);
        Assert.True(pack.Broken);
        Assert.Empty(pack.OldVersions);
    }

    [Fact]
    public async Task Fetch_UnparseableXml_FailsWithListName()
    {
        _server.ModpacksXml = "this is not xml";
        _server.ThirdPartyXml = """<modpacks></modpacks>""";

        var task = CreateFetchTask();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task.FetchAsync(CancellationToken.None));

        Assert.Contains("Failed to download some pack lists:", ex.Message);
        Assert.Contains("Public Packs", ex.Message);
        Assert.DoesNotContain("Third Party Packs", ex.Message);
    }

    [Fact]
    public async Task FetchPrivate_CarriesPackCode()
    {
        _server.PrivateXmls["secret"] = """
                                        <?xml version="1.0" encoding="UTF-8"?>
                                        <modpacks>
                                            <modpack name="Private Pack" version="9.9" mcVersion="1.7.10"
                                                     oldVersions="9.8" author="carol" dir="PrivatePack" url="PrivatePack-9.9.zip"/>
                                        </modpacks>
                                        """;

        var task = CreateFetchTask();
        var fetched = new List<LegacyFtbModpack>();
        task.PrivatePackFetched = fetched.Add;
        await task.FetchPrivateAsync(["secret"]);

        var pack = Assert.Single(fetched);
        Assert.Equal(PackType.Private, pack.Type);
        Assert.Equal("secret", pack.PackCode);
        Assert.Equal("Private Pack", pack.Name);
    }

    // ------------------------------------------------------------ private pack codes

    [Fact]
    public void PrivatePackManager_RoundTrip()
    {
        var path = Path.Combine(_root, "private_packs.txt");
        var manager = new PrivatePackManager(path);
        Assert.True(manager.Empty);
        manager.Load(); // missing file -> empty
        Assert.True(manager.Empty);

        manager.Add("code1");
        manager.Add("code2");
        manager.Save();

        var reloaded = new PrivatePackManager(path);
        reloaded.Load();
        Assert.False(reloaded.Empty);
        Assert.Equal(new[] { "code1", "code2" }.OrderBy(c => c),
            reloaded.GetCurrentPackCodes().OrderBy(c => c));

        // one code per line (the C++ file format)
        var content = File.ReadAllLines(path);
        Assert.Equal(2, content.Length);

        Assert.True(reloaded.Remove("code1"));
        reloaded.Save();
        var afterRemove = new PrivatePackManager(path);
        afterRemove.Load();
        Assert.Equal(new[] { "code2" }, afterRemove.GetCurrentPackCodes());
    }

    // ----------------------------------------------------------------------- install

    /// <summary>Builds a pack archive zip with the legacy layout (minecraft/ + instMods/).</summary>
    private static string BuildArchive(string path, Action<string> populate)
    {
        var srcDir = Path.Combine(Path.GetTempPath(), $"flint-ftb-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        populate(srcDir);
        var zipPath = path;
        Assert.True(MmcZip.CompressDir(new AbsolutePath(zipPath), new AbsolutePath(srcDir)));
        try
        {
            Directory.Delete(srcDir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }

        return zipPath;
    }

    private static LegacyFtbModpack MakePack(string dir = "TestPack", string file = "TestPack-1_2_3.zip",
        PackType type = PackType.Public, string mcVersion = "1.7.10")
    {
        return new LegacyFtbModpack
        {
            Name = "Test Pack",
            Dir = dir,
            File = file,
            Type = type,
            McVersion = mcVersion,
            CurrentVersion = "1.2.3",
        };
    }

    [Fact]
    public async Task Install_ForgeFromPackJson_CreatesInstance()
    {
        var pack = MakePack();
        _server.Archives["modpacks/TestPack/1_2_3/TestPack-1_2_3.zip"] = BuildArchive(
            Path.Combine(_root, "forge-pack.zip"), srcDir =>
            {
                Directory.CreateDirectory(Path.Combine(srcDir, "minecraft", "config"));
                File.WriteAllText(Path.Combine(srcDir, "minecraft", "pack.json"),
                    """{"libraries":[{"name":"net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10"}]}""");
                File.WriteAllText(Path.Combine(srcDir, "minecraft", "config", "test.cfg"), "ftb=1\n");
                Directory.CreateDirectory(Path.Combine(srcDir, "minecraft", "mods"));
            });

        var list = CreateList();
        var task = new LegacyFtbPackInstallTask(pack, "1.2.3", TestNet.Http) { CdnBaseUrl = _server.Url + "/" };
        task.SetName("Legacy FTB Forge");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);


        var staging = Path.Combine(_root, "instances", "Legacy FTB Forge");
        // the minecraft folder of the archive became the instance's minecraft dir
        Assert.Equal("ftb=1\n",
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "config", "test.cfg")));
        // the pack.json was consumed (deleted)
        Assert.False(File.Exists(Path.Combine(staging, "minecraft", "pack.json")));
        // the unzip working folder was nuked
        Assert.False(Directory.Exists(Path.Combine(staging, "unzip")));

        var components = Components(staging);
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.7.10" });
        // "1.7.10-10.13.4.1614-1.7.10" minus the mc version and dashes -> the forge build
        Assert.Contains(components, c => c is { Uid: "net.minecraftforge", Version: "10.13.4.1614" });
    }

    [Fact]
    public async Task Install_JarModsFallBack_CreatesInstance()
    {
        var pack = MakePack(file: "TestPack-2_0_0.zip");
        _server.Archives["modpacks/TestPack/2_0_0/TestPack-2_0_0.zip"] = BuildArchive(
            Path.Combine(_root, "jarmod-pack.zip"), srcDir =>
            {
                Directory.CreateDirectory(Path.Combine(srcDir, "instMods"));
                File.WriteAllBytes(Path.Combine(srcDir, "instMods", "legacycore.jar"),
                    "legacy jar mod payload"u8.ToArray());
                Directory.CreateDirectory(Path.Combine(srcDir, "minecraft", "mods"));
                File.WriteAllBytes(Path.Combine(srcDir, "minecraft", "mods", "oldmod.jar"),
                    "old mod payload"u8.ToArray());
            });

        var list = CreateList();
        var task = new LegacyFtbPackInstallTask(pack, "2.0.0", TestNet.Http) { CdnBaseUrl = _server.Url + "/" };
        task.SetName("Legacy FTB JarMods");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);


        var staging = Path.Combine(_root, "instances", "Legacy FTB JarMods");
        Assert.Equal("old mod payload",
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "mods", "oldmod.jar")));

        var components = Components(staging);
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.7.10" });
        Assert.Contains(components, c => c.Uid is not null && c.Uid.StartsWith("custom.jarmod.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_PrivatePack_DownloadsFromPrivatePacks()
    {
        var pack = MakePack(dir: "SecretPack", file: "SecretPack-1_0.zip", type: PackType.Private);
        _server.Archives["privatepacks/SecretPack/1_0/SecretPack-1_0.zip"] = BuildArchive(
            Path.Combine(_root, "private-pack.zip"), srcDir =>
            {
                Directory.CreateDirectory(Path.Combine(srcDir, "instMods"));
                File.WriteAllBytes(Path.Combine(srcDir, "instMods", "core.jar"), "core"u8.ToArray());
            });

        var list = CreateList();
        var task = new LegacyFtbPackInstallTask(pack, "1.0", TestNet.Http) { CdnBaseUrl = _server.Url + "/" };
        task.SetName("Legacy FTB Private");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        Assert.Equal("privatepacks/SecretPack/1_0/SecretPack-1_0.zip", _server.LastArchivePath);
    }

    [Fact]
    public async Task Install_NoInstallMethod_Fails()
    {
        var pack = MakePack(file: "TestPack-0_1.zip");
        _server.Archives["modpacks/TestPack/0_1/TestPack-0_1.zip"] = BuildArchive(
            Path.Combine(_root, "bare-pack.zip"), srcDir =>
            {
                Directory.CreateDirectory(Path.Combine(srcDir, "minecraft"));
                File.WriteAllText(Path.Combine(srcDir, "minecraft", "options.txt"), "nothing:1\n");
            });

        var list = CreateList();
        var task = new LegacyFtbPackInstallTask(pack, "0.1", TestNet.Http) { CdnBaseUrl = _server.Url + "/" };
        task.SetName("Legacy FTB Bare");
        var wrapped = list.WrapInstanceTask(task);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => wrapped(TestContext.Current.CancellationToken));
        Assert.Equal("No installation method found!", ex.Message);
    }
}

/// <summary>
/// Routes a local HttpListener as a legacy FTB CDN stub:
///   GET /static/modpacks.xml       -> ModpacksXml
///   GET /static/thirdparty.xml     -> ThirdPartyXml
///   GET /static/{code}.xml         -> PrivateXmls[code]
///   GET /modpacks/... or /privatepacks/... -> Archives[path]
/// </summary>
internal sealed class StubLegacyFtbServer : IDisposable
{
    private readonly HttpListener _listener;

    public StubLegacyFtbServer()
    {
        // TestPorts owns the port lease AND the rebind-retry (HttpListener cannot adopt the
        // lease socket, so losing the takeover window retries on a fresh port)
        (_listener, Url) = PortAllocator.StartHttpListener();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    try
                    {
                        Handle(context);
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        });
    }

    public string Url { get; }

    public string ModpacksXml { get; set; } = "<modpacks></modpacks>";
    public string ThirdPartyXml { get; set; } = "<modpacks></modpacks>";
    public Dictionary<string, string> PrivateXmls { get; } = new(StringComparer.Ordinal);

    /// <summary>Archive path suffix (e.g. "modpacks/{dir}/{ver}/{file}") -> zip file path.</summary>
    public Dictionary<string, string> Archives { get; } = new(StringComparer.Ordinal);

    public string LastArchivePath { get; private set; } = "";

    private void Handle(HttpListenerContext ctx)
    {
        var url = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
        var path = url.AbsolutePath.TrimStart('/');

        byte[] body;
        if (path == "static/modpacks.xml")
        {
            body = Encoding.UTF8.GetBytes(ModpacksXml);
        }
        else if (path == "static/thirdparty.xml")
        {
            body = Encoding.UTF8.GetBytes(ThirdPartyXml);
        }
        else if (path.StartsWith("static/", StringComparison.Ordinal)
                 && path.EndsWith(".xml", StringComparison.Ordinal))
        {
            var code = path["static/".Length..^".xml".Length];
            if (!PrivateXmls.TryGetValue(code, out var privateXml))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            body = Encoding.UTF8.GetBytes(privateXml);
        }
        else
        {
            if (!Archives.TryGetValue(path, out var archivePath))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            LastArchivePath = path;
            body = File.ReadAllBytes(archivePath);
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
    }

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException) { }
    }
}
