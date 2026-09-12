// SPDX-License-Identifier: GPL-3.0-only
// Tests for the FTB App API platform: the /modpack/all index, the pack description and the
// version payload parsing, the data-level pack model (dud-pack skipping + search filter)
// and the end-to-end pack install against the stub servers (instance layout, component
// ids, files by their path/type, sha1 verification, CurseForge resolution and the blocked
// file flow) — the Flame stub backs the CurseForge file resolver.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.ModPlatform.Ftb;
using Xunit;

namespace Flint.Tests;

[Collection("FlameApi")]
public class FtbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-ftb-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubFtbServer _server = new();
    private readonly StubFlameServer _flameServer = new();
    private readonly byte[] _cfBytes = "ftb curseforge-resolved mod payload"u8.ToArray();

    public FtbTests()
    {
        Directory.CreateDirectory(_root);
        FtbApi.BaseUrl = _server.Url;
        // the FTB install resolves CurseForge-only files through the Flame layer
        FlintLauncher.Core.ModPlatform.Flame.FlameConfig.BaseUrl = _flameServer.Url;
    }

    public void Dispose()
    {
        FtbApi.BaseUrl = FlintLauncher.Core.FlintConfig.Default.FtbApiBaseUrl;
        FlintLauncher.Core.ModPlatform.Flame.FlameConfig.BaseUrl = FlintLauncher.Core.FlintConfig.Default.FlameBaseUrl;
        _server.Dispose();
        _flameServer.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private InstanceList CreateList()
    {
        var instancesDir = Path.Combine(_root, "instances");
        Directory.CreateDirectory(instancesDir);
        return new InstanceList(_global, [instancesDir]);
    }

    private static List<(string? Uid, string? Version)> Components(string staging)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "mmc-pack.json")));
        return doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(),
                          Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
            .ToList();
    }

    // ----------------------------------------------------------------- index/manifests

    [Fact]
    public async Task Index_ParsesPackIds()
    {
        _server.ModpacksIndexJson = """{"packs":[42, 43]}""";

        var (job, request) = FtbApi.FetchModpacks();
        Assert.Equal(FtbApi.ModpacksUrl(), request.Url.ToString());
        await job.StartAsync();
        Assert.True(job.WasSuccessful, job.FailReason);

        var packs = FtbApi.LoadModpackIndex(request.BodyAsString);
        Assert.Equal(new[] { 42, 43 }, packs);
    }

    [Fact]
    public async Task Modpack_ParsesDescription()
    {
        var fixture = FtbPackFixture.Create();
        _server.Modpacks[FtbPackFixture.PackId.ToString()] = fixture.PackJson;

        var (job, request) = FtbApi.FetchModpack(FtbPackFixture.PackId);
        Assert.Equal(FtbApi.ModpackUrl(FtbPackFixture.PackId), request.Url.ToString());
        await job.StartAsync();
        Assert.True(job.WasSuccessful, job.FailReason);

        var pack = FtbApi.LoadModpack(request.BodyAsString);

        Assert.Equal(42, pack.Id);
        Assert.Equal("Test FTB Pack", pack.Name);
        Assert.Equal("a synthetic pack", pack.Synopsis);
        Assert.Equal("a synthetic pack description", pack.Description);
        Assert.True(pack.Featured);
        Assert.Equal(100, pack.Installs);
        Assert.Equal(200, pack.Plays);
        Assert.Equal("testftbpack.png", pack.SafeName);
        var art = Assert.Single(pack.Art);
        Assert.Equal("square", art.Type);
        Assert.Equal("https://example.invalid/logo.png", art.Url);
        Assert.Equal(100, art.Width);
        Assert.False(art.Compressed);
        var author = Assert.Single(pack.Authors);
        Assert.Equal("tester", author.Name);
        Assert.Equal(2, pack.Versions.Count);
        Assert.Equal(FtbPackFixture.VersionId, pack.Versions[0].Id);
        Assert.Equal("1.0.0", pack.Versions[0].Name);
        Assert.Equal("Release", pack.Versions[0].Type);
        Assert.Equal(2048, pack.Versions[0].Specs.Minimum);
        Assert.Equal(4096, pack.Versions[0].Specs.Recommended);
        Assert.Equal("magic", Assert.Single(pack.Tags).Name);
    }

    [Fact]
    public async Task Version_ParsesPayload()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);

        var (job, request) = FtbApi.FetchVersion(FtbPackFixture.PackId, FtbPackFixture.VersionId);
        await job.StartAsync();
        Assert.True(job.WasSuccessful, job.FailReason);

        var version = FtbApi.LoadVersion(request.BodyAsString);

        Assert.Equal(FtbPackFixture.VersionId, version.Id);
        Assert.Equal(42, version.Parent);
        Assert.Equal("1.0.0", version.Name);
        Assert.Equal(2, version.Targets.Count);
        Assert.Equal("game", version.Targets[0].Type);
        Assert.Equal("minecraft", version.Targets[0].Name);
        Assert.Equal("1.18.2", version.Targets[0].Version);
        Assert.Equal("modloader", version.Targets[1].Type);
        Assert.Equal("forge", version.Targets[1].Name);
        Assert.Equal("40.1.0", version.Targets[1].Version);
        Assert.Equal(4, version.Files.Count);
        var mod = version.Files[0];
        Assert.Equal("mods", mod.Path);
        Assert.Equal("testmod.jar", mod.Name);
        Assert.False(mod.ServerOnly);
        Assert.Equal(fixture.ModSha1, mod.Sha1);
        // the curseforge pair of the third file was read
        Assert.Equal(0, version.Files[2].Url.Length);
        Assert.Equal(123, version.Files[2].Curseforge.ProjectId);
        Assert.Equal(456, version.Files[2].Curseforge.FileId);
        // the server-only flag of the fourth file was read
        Assert.True(version.Files[3].ServerOnly);
    }

    // ----------------------------------------------------------------------- model

    [Fact]
    public async Task Model_Fetch_SkipsPackWithoutVersionsAndFilters()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        // a second pack without any versions: a "dud" the model must ignore
        var dudJson = Regex.Replace(fixture.PackJson, "\"versions\":\\[.*?\\]", "\"versions\":[]",
            RegexOptions.Singleline);
        _server.ModpacksIndexJson = """{"packs":[42, 43]}""";
        _server.Modpacks["43"] = dudJson;

        var model = new FtbPackModel();
        await model.FetchAsync();

        var pack = Assert.Single(model.Packs);
        Assert.Equal(42, pack.Id);

        Assert.Single(model.FilteredPacks(""));
        Assert.Single(model.FilteredPacks("test ftb"));
        Assert.Empty(model.FilteredPacks("nomatch"));
    }

    // --------------------------------------------------------------------- install

    private void SetupFlameResolution(string? downloadUrlPath = "mod.bin")
    {
        var fileJson = FlamePayloads.FileJson(_flameServer.Url, fileId: 456,
            fileName: "cfmod.jar", displayName: "CF Mod 2.0",
            downloadUrlPath: downloadUrlPath);
        _flameServer.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(fileJson)),
        }.ToJsonString();
        _flameServer.ProjectsJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.ProjectJson(id: 123))),
        }.ToJsonString();
        _flameServer.Downloads["mod.bin"] = _cfBytes;
    }

    [Fact]
    public async Task Install_EndToEnd_CreatesInstanceWithFilesByPath()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        SetupFlameResolution();

        var list = CreateList();
        var task = new FtbPackInstallTask(FtbApi.LoadModpack(fixture.PackJson), "1.0.0");
        task.SetName("FTB Imported");
        task.SetIcon("ftb_logo");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "FTB Imported");
        // instance layout: the files went below .minecraft/<path>/<name> per their type
        Assert.Equal(fixture.ModBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, ".minecraft", "mods", "testmod.jar")));
        Assert.Equal(fixture.ConfigBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, ".minecraft", "config", "test.cfg")));
        // the CurseForge-only file was resolved through the Flame layer and downloaded
        Assert.Equal(_cfBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, ".minecraft", "mods", "cfmod.jar")));
        // the server-only file was skipped
        Assert.False(File.Exists(Path.Combine(staging, ".minecraft", "mods", "servermod.jar")));

        // components: net.minecraft + the forge modloader target
        var components = Components(staging);
        Assert.Contains(components, c => c.Uid == "net.minecraft" && c.Version == "1.18.2");
        Assert.Contains(components, c => c.Uid == "net.minecraftforge" && c.Version == "40.1.0");
        Assert.DoesNotContain(components, c => c.Uid == "net.fabricmc.fabric-loader");

        // managed-pack bookkeeping
        Assert.Contains("ManagedPackType=ftb", File.ReadAllText(Path.Combine(staging, "instance.cfg")));
    }

    [Fact]
    public async Task Install_Sha1Mismatch_FailsTask()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        SetupFlameResolution();
        // corrupt the served mod: the sha1 validator must fail the download job
        _server.Downloads["testmod.jar"] = "corrupted payload!!"u8.ToArray();

        var list = CreateList();
        var task = new FtbPackInstallTask(FtbApi.LoadModpack(fixture.PackJson), "1.0.0");
        task.SetName("FTB Sha1");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.False(task.WasSuccessful);
        Assert.False(File.Exists(Path.Combine(_root, "instances", "FTB Sha1",
            ".minecraft", "mods", "testmod.jar")));
    }

    [Fact]
    public async Task Install_CurseforgeFileWithoutUrl_IsBlockedAndFailsUnmatched()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        // the CF API answered without a download URL -> a blocked file
        SetupFlameResolution(downloadUrlPath: null);

        var list = CreateList();
        var task = new FtbPackInstallTask(FtbApi.LoadModpack(fixture.PackJson), "1.0.0");
        task.SetName("FTB Blocked");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.False(task.WasSuccessful);
        Assert.Contains("not available for download", task.FailReason);
        Assert.Contains("cfmod.jar", task.FailReason);
        var blocked = Assert.Single(task.BlockedMods);
        Assert.Equal("cfmod.jar", blocked.Name);
        Assert.Equal("mods", blocked.TargetFolder);
    }

    [Fact]
    public async Task Install_BlockedFileMatchedLocally_IsCopiedIntoInstance()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        SetupFlameResolution(downloadUrlPath: null);

        var localPath = Path.Combine(_root, "downloaded-cfmod.jar");
        await File.WriteAllBytesAsync(localPath, _cfBytes);

        var list = CreateList();
        var task = new FtbPackInstallTask(FtbApi.LoadModpack(fixture.PackJson), "1.0.0")
        {
            BlockedModLocalFileResolver = _ => localPath,
        };
        task.SetName("FTB Matched");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "FTB Matched");
        Assert.True(task.BlockedMods[0].Matched);
        Assert.Equal(_cfBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, ".minecraft", "mods", "cfmod.jar")));
    }

    [Fact]
    public async Task Install_UnknownVersionName_FailsTask()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);

        var list = CreateList();
        var task = new FtbPackInstallTask(FtbApi.LoadModpack(fixture.PackJson), "no-such-version");
        task.SetName("FTB UnknownVersion");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.False(task.WasSuccessful);
        Assert.Contains("Failed to find pack version no-such-version", task.FailReason);
    }
}
