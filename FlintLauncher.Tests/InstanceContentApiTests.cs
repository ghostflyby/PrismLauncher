// SPDX-License-Identifier: GPL-3.0-only
// UI-API mapping Wave A tests: host the real web app in-process (same pattern as
// ApiIntegrationTests) and drive the new instance-content surface — instances search/group
// filters + /instances/groups, the copy flow (202 task), worlds CRUD/rename/install,
// servers.dat CRUD + the status ping against a local TCP stub, resources/{type}
// enable/disable/delete, screenshots listing/upload/delete and the options.txt surface.
//
// Every class joins "LauncherJars": the hosts chdir into a temp data dir
// (Environment.CurrentDirectory is process-global) and must not interleave with the other
// API-host suites.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Minecraft.Worlds;
using FlintLauncher.Nbt;
using FlintLauncher.Web;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace FlintLauncher.Tests;

// --------------------------------------------------------------------------- host fixture

/// <summary>Like FlintApiHost, but the temp home's data root is created (and seeded) by the
/// caller BEFORE the host starts — the instance list loads once in the runtime constructor.
/// The caller passes the home root; instances live under &lt;home&gt;/data/instances (the
/// G1 home layout).</summary>
internal sealed class WaveApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _previousCwd;

    private WaveApiHost(WebApplication app, HttpClient client, string homeDir, string dataDir,
        string previousCwd)
    {
        _app = app;
        Client = client;
        HomeDir = homeDir;
        DataDir = dataDir;
        _previousCwd = previousCwd;
    }

    public HttpClient Client { get; }

    /// <summary>The temp home root (passed as --home-dir; cleaned up on dispose).</summary>
    public string HomeDir { get; }

    /// <summary>The data root (home layout: &lt;home&gt;/data) — where instances live.</summary>
    public string DataDir { get; }

    public string GameRootOf(string folder) =>
        Path.Combine(DataDir, "instances", folder, "minecraft");

    public static async Task<WaveApiHost> StartAsync(string homeDir)
    {
        Directory.CreateDirectory(homeDir);
        var dataDir = Path.Combine(homeDir, "data");
        Directory.CreateDirectory(dataDir);
        var previousCwd = Environment.CurrentDirectory;
        // S2: the token is host-injected (--api-token) — no token file exists to read
        var token = Guid.NewGuid().ToString("N");
        var app = WebHostBuilder.BuildWebApplication(
        [
            "--home-dir", homeDir,
            "--api-token", token,
            "--urls", "http://127.0.0.1:0",
        ]);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Api-Token", token);
        return new WaveApiHost(app, client, homeDir, dataDir, previousCwd);
    }

    public async Task<HttpResponseMessage> PostAsync<T>(string path, T body) =>
        await Client.PostAsJsonAsync(path, body);

    public async Task<HttpResponseMessage> PutAsync<T>(string path, T body) =>
        await Client.PutAsJsonAsync(path, body);

    /// <summary>Poll GET /api/v1/tasks/{id} until the given state (or a terminal failure).</summary>
    public async Task<JsonElement> WaitForTaskAsync(string taskId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement task = default;
        while (DateTime.UtcNow < deadline)
        {
            var response = await Client.GetAsync($"/api/v1/tasks/{taskId}");
            response.EnsureSuccessStatusCode();
            task = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            var state = task.GetProperty("state").GetString();
            if (state is "succeeded" or "failed" or "aborted")
            {
                Assert.Equal("succeeded", state);
                return task;
            }
            await Task.Delay(50);
        }
        throw new Exception($"task {taskId} never finished, last: {task.GetRawText()}");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();
            var restoreTo = Directory.Exists(_previousCwd) ? _previousCwd : Path.GetTempPath();
            Environment.CurrentDirectory = restoreTo;
            try
            {
                Directory.Delete(HomeDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}

// --------------------------------------------------------------------------- seed helpers

internal static class WaveSeed
{
    /// <summary>The data root's instances dir for a home root (the G1 home layout).</summary>
    public static string InstancesDir(string homeDir) =>
        Path.Combine(homeDir, "data", "instances");

    /// <summary>A minimal OneSix instance on disk (patches + mmc-pack like the launch e2e);
    /// must run before the host starts. <paramref name="homeDir"/> is the temp home root.</summary>
    public static string Instance(string homeDir, string folder, string name)
    {
        var instanceRoot = Path.Combine(InstancesDir(homeDir), folder);
        Directory.CreateDirectory(instanceRoot);
        File.WriteAllText(Path.Combine(instanceRoot, "instance.cfg"), $"name={name}\n");
        Directory.CreateDirectory(Path.Combine(instanceRoot, "patches"));
        File.WriteAllText(Path.Combine(instanceRoot, "patches", "net.minecraft.json"),
            LaunchFixture.DefaultPatchJson);
        File.WriteAllText(Path.Combine(instanceRoot, "mmc-pack.json"),
            """{"formatVersion":1,"components":[{"important":true,"uid":"net.minecraft"}]}""");
        return instanceRoot;
    }

    /// <summary>Worlds/<World>.h compatible level.dat: Data.LevelName/LastPlayed/GameType.</summary>
    public static void World(string worldDir, string name, long lastPlayed = 1700000000000,
        int gameType = 1)
    {
        Directory.CreateDirectory(worldDir);
        var data = new NbtCompound();
        data.Put("LevelName", new NbtString(name));
        data.Put("LastPlayed", new NbtLong(lastPlayed));
        data.Put("GameType", new NbtInt(gameType));
        var root = new NbtCompound();
        root.Put("Data", data);
        File.WriteAllBytes(Path.Combine(worldDir, "level.dat"), NbtIo.WriteCompoundGzip(root));
    }

    /// <summary>A zip with a single level.dat at its root (the world-upload payload).</summary>
    public static string WorldZip(string path, string name)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("level.dat");
        var data = new NbtCompound();
        data.Put("LevelName", new NbtString(name));
        data.Put("LastPlayed", new NbtLong(1700000000000));
        var root = new NbtCompound();
        root.Put("Data", data);
        using var stream = entry.Open();
        var bytes = NbtIo.WriteCompoundGzip(root);
        stream.Write(bytes, 0, bytes.Length);
        return path;
    }
}

// ------------------------------------------------------------------------------- tests

[Collection("LauncherJars")]
public class InstanceQueryApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-wave-{Guid.NewGuid():N}");
    private WaveApiHost? host;

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Instances_Search_And_Group_Filters()
    {
        WaveSeed.Instance(_dataDir, "alpha", "Alpha One");
        WaveSeed.Instance(_dataDir, "beta", "Beta Two");
        WaveSeed.Instance(_dataDir, "gamma", "Gamma Three");
        host = await WaveApiHost.StartAsync(_dataDir);

        // groups are assigned through the existing PATCH endpoint
        await PatchGroup("alpha", "Packs");
        await PatchGroup("beta", "Packs");

        // name-contains filter (case-insensitive) -----------------------------------
        var list = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances?search=beta")).RootElement;
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Beta Two", list[0].GetProperty("name").GetString());

        // group-equals filter ---------------------------------------------------------
        list = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances?group=Packs")).RootElement;
        Assert.Equal(2, list.GetArrayLength());
        Assert.DoesNotContain(list.EnumerateArray(),
            e => e.GetProperty("name").GetString() == "Gamma Three");

        // combined filters narrow further ----------------------------------------------
        list = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances?search=alpha&group=Packs")).RootElement;
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Alpha One", list[0].GetProperty("name").GetString());

        // no hit: empty array, not an error ------------------------------------------
        list = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances?search=zzz")).RootElement;
        Assert.Equal(0, list.GetArrayLength());
    }

    private async Task PatchGroup(string id, string group)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/instances/{id}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { group }), Encoding.UTF8, "application/json"),
        };
        var response = await host!.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Instances_Groups_Index()
    {
        WaveSeed.Instance(_dataDir, "one", "One");
        WaveSeed.Instance(_dataDir, "two", "Two");
        WaveSeed.Instance(_dataDir, "loose", "Loose");
        host = await WaveApiHost.StartAsync(_dataDir);

        // assign groups through the PATCH endpoint (the group definitions live in the C8
        // groups file, members as uuid references)
        await PatchGroup("one", "Packs");
        await PatchGroup("two", "Packs");

        var groups = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/groups")).RootElement;
        Assert.Equal(1, groups.GetArrayLength());
        Assert.Equal("Packs", groups[0].GetProperty("name").GetString());
        // DATA D3: the members are uuid references (the persistent form)
        var uuidOf = await host.Client.GetStringAsync("/api/v1/instances");
        var uuids = JsonDocument.Parse(uuidOf).RootElement.EnumerateArray()
            .Where(e => new[] { "one", "two" }.Contains(e.GetProperty("id").GetString()))
            .Select(e => e.GetProperty("uuid").GetString())
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();
        var ids = groups[0].GetProperty("instanceIds").EnumerateArray()
            .Select(i => i.GetString()).OrderBy(u => u, StringComparer.Ordinal).ToList();
        Assert.Equal(uuids, ids);
    }
}

[Collection("LauncherJars")]
public class InstanceCopyApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-wave-{Guid.NewGuid():N}");
    private WaveApiHost? host;

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Copy_With_Prefs_Filters_Selected_Content()
    {
        // seed through the vanilla flow so instance.cfg is fully formed, then add content
        host = await WaveApiHost.StartAsync(_dataDir);
        var create = await host.PostAsync("/api/v1/instances/vanilla",
            new { name = "Copy Src", mcVersion = "1.20.1" });
        create.EnsureSuccessStatusCode();
        var srcId = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var gameRoot = host.GameRootOf(srcId);
        Directory.CreateDirectory(Path.Combine(gameRoot, "mods"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "mods", "kept.jar"), "mod");
        Directory.CreateDirectory(Path.Combine(gameRoot, "saves", "World"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "saves", "World", "level.dat"), "level");
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "options.txt"), "difficulty=hard");

        // copy without saves/mods, resetting playtime, into its own group
        var copy = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(srcId)}/copy",
            new { name = "Copy Tgt", group = "Copies", copySaves = false, copyMods = false,
                  keepPlaytime = false });
        Assert.Equal(HttpStatusCode.Accepted, copy.StatusCode);
        var taskId = JsonDocument.Parse(await copy.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;
        await host.WaitForTaskAsync(taskId, TimeSpan.FromSeconds(20));

        // the copy is discoverable (the finished task refreshed the list) and named
        var list = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances?search=Copy%20Tgt")).RootElement;
        Assert.Equal(1, list.GetArrayLength());
        var copyId = list[0].GetProperty("id").GetString()!;
        Assert.NotEqual(srcId, copyId);
        Assert.Equal("Copies", list[0].GetProperty("group").GetString());

        // the prefs filters: options.txt kept, saves/ + mods/ excluded
        var copyRoot = Path.Combine(WaveSeed.InstancesDir(_dataDir), copyId, "minecraft");
        Assert.True(File.Exists(Path.Combine(copyRoot, "options.txt")));
        Assert.False(Directory.Exists(Path.Combine(copyRoot, "saves")));
        Assert.False(Directory.Exists(Path.Combine(copyRoot, "mods")));

        // the source is untouched
        Assert.True(Directory.Exists(Path.Combine(gameRoot, "saves")));

        // invalid body: missing name -> 400
        var invalid = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(srcId)}/copy",
            new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}

[Collection("LauncherJars")]
public class WorldApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-wave-{Guid.NewGuid():N}");
    private WaveApiHost? host;

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task<WaveApiHost> StartHostWithInstance()
    {
        WaveSeed.Instance(_dataDir, "worlds-inst", "Worlds Inst");
        WaveSeed.World(Path.Combine(WaveSeed.InstancesDir(_dataDir), "worlds-inst", "minecraft", "saves",
            "world_one"), "World One");
        return await WaveApiHost.StartAsync(_dataDir);
    }

    [Fact]
    public async Task Worlds_List_Rename_Delete_RoundTrip()
    {
        host = await StartHostWithInstance();

        // list: metadata parsed from the seeded level.dat -----------------------------
        var worlds = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/worlds-inst/worlds")).RootElement;
        Assert.Equal(1, worlds.GetArrayLength());
        var world = worlds[0];
        Assert.Equal("World One", world.GetProperty("name").GetString());
        Assert.Equal("world_one", world.GetProperty("folderName").GetString());
        Assert.Equal(1700000000000, world.GetProperty("lastPlayed").GetInt64());
        Assert.Equal("Creative", world.GetProperty("gameType").GetString());
        Assert.True(world.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.EndsWith("world_one", world.GetProperty("containerPath").GetString());

        // rename: level.dat LevelName rewritten + folder renamed ----------------------
        var rename = await host.PostAsync(
            "/api/v1/instances/worlds-inst/worlds/world_one/rename", new { newName = "Renamed One" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(
            host.GameRootOf("worlds-inst"), "saves", "Renamed One")));
        worlds = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/worlds-inst/worlds")).RootElement;
        Assert.Equal("Renamed One", worlds[0].GetProperty("name").GetString());
        Assert.Equal("Renamed One", worlds[0].GetProperty("folderName").GetString());

        // delete -----------------------------------------------------------------------
        var delete = await host.Client.DeleteAsync(
            "/api/v1/instances/worlds-inst/worlds/Renamed%20One");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(
            host.GameRootOf("worlds-inst"), "saves", "Renamed One")));

        // errors: unknown world 404, missing instance 404 ------------------------------
        var missing = await host.Client.DeleteAsync(
            "/api/v1/instances/worlds-inst/worlds/Ghost");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("world_not_found", JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
        var noInst = await host.Client.GetAsync("/api/v1/instances/ghost/worlds");
        Assert.Equal(HttpStatusCode.NotFound, noInst.StatusCode);
    }

    [Fact]
    public async Task Worlds_Install_From_Multipart_Zip()
    {
        host = await StartHostWithInstance();

        var zipPath = WaveSeed.WorldZip(Path.Combine(Path.GetTempPath(),
            $"flint-wave-zip-{Guid.NewGuid():N}.zip"), "Uploaded World");
        try
        {
            using var form = new MultipartFormDataContent();
            var zipBytes = await File.ReadAllBytesAsync(zipPath);
            var content = new ByteArrayContent(zipBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(content, "file", "uploaded.zip");

            var install = await host.Client.PostAsync(
                "/api/v1/instances/worlds-inst/worlds/install", form);
            Assert.Equal(HttpStatusCode.Created, install.StatusCode);
            var created = JsonDocument.Parse(await install.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("Uploaded World", created.GetProperty("name").GetString());
            Assert.True(Directory.Exists(Path.Combine(
                host.GameRootOf("worlds-inst"), "saves", "Uploaded World")));

            // and it shows up in the listing
            var worlds = JsonDocument.Parse(await host.Client.GetStringAsync(
                "/api/v1/instances/worlds-inst/worlds")).RootElement;
            Assert.Contains(worlds.EnumerateArray(),
                w => w.GetProperty("name").GetString() == "Uploaded World");
        }
        finally
        {
            File.Delete(zipPath);
        }

        // non-world payload -> 400 -----------------------------------------------------
        using var badForm = new MultipartFormDataContent();
        var junk = new ByteArrayContent([0x00, 0x01, 0x02]);
        badForm.Add(junk, "file", "not-a-world.zip");
        var bad = await host.Client.PostAsync("/api/v1/instances/worlds-inst/worlds/install", badForm);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("world_install_failed", JsonDocument.Parse(await bad.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

[Collection("LauncherJars")]
public class ServerApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-wave-{Guid.NewGuid():N}");
    private WaveApiHost? host;

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task<WaveApiHost> StartHostWithInstance()
    {
        WaveSeed.Instance(_dataDir, "servers-inst", "Servers Inst");
        return await WaveApiHost.StartAsync(_dataDir);
    }

    [Fact]
    public async Task Servers_Crud_And_Persistence()
    {
        host = await StartHostWithInstance();

        // missing servers.dat -> empty list --------------------------------------------
        var list = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/servers-inst/servers")).RootElement;
        Assert.Equal(0, list.GetArrayLength());

        // add two servers (201 + views) -------------------------------------------------
        var add = await host.PostAsync("/api/v1/instances/servers-inst/servers",
            new { name = "Hypixel", address = "mc.hypixel.net" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var first = JsonDocument.Parse(await add.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, first.GetProperty("index").GetInt32());
        Assert.Equal("Hypixel", first.GetProperty("name").GetString());
        Assert.Equal("ask", first.GetProperty("acceptsTextures").GetString());
        Assert.False(first.GetProperty("hasIcon").GetBoolean());

        await host.PostAsync("/api/v1/instances/servers-inst/servers",
            new { name = "Local", address = "127.0.0.1:25565" });

        // persistence: the list round-trips through the servers.dat file on disk
        var serversPath = Path.Combine(host.GameRootOf("servers-inst"), "servers.dat");
        Assert.True(File.Exists(serversPath));
        var persisted = ServersDat.Load(serversPath);
        Assert.Equal(2, persisted.Count);
        Assert.Equal("mc.hypixel.net", persisted[0].Address);

        // delete by index: the second entry shifts -------------------------------------
        var delete = await host.Client.DeleteAsync("/api/v1/instances/servers-inst/servers/0");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        list = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/servers-inst/servers")).RootElement;
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Local", list[0].GetProperty("name").GetString());
        Assert.Equal(0, list[0].GetProperty("index").GetInt32());

        // errors: bad index 404, bad body 400 ------------------------------------------
        var badIndex = await host.Client.DeleteAsync("/api/v1/instances/servers-inst/servers/9");
        Assert.Equal(HttpStatusCode.NotFound, badIndex.StatusCode);
        var badBody = await host.PostAsync("/api/v1/instances/servers-inst/servers",
            new { name = "No Address" });
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
    }

    [Fact]
    public async Task Server_Ping_Status_Handshake_Against_Local_Stub()
    {
        // a raw TCP stub speaking the wiki.vg status protocol (the handshake is answered
        // with a varInt-framed status JSON, like a real server)
        var statusJson = """{"version":{"name":"1.20.1"},"players":{"online":3,"max":20},"description":"A test server"}""";
        using var stub = new PingStubServer(BuildStatusReply(statusJson));

        host = await StartHostWithInstance();
        await host.PostAsync("/api/v1/instances/servers-inst/servers",
            new { name = "Stub", address = $"127.0.0.1:{stub.Port}" });

        var response = await host.Client.PostAsync(
            "/api/v1/instances/servers-inst/servers/0/ping", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ping = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal($"127.0.0.1:{stub.Port}", ping.GetProperty("host").GetString());
        Assert.Equal("127.0.0.1", ping.GetProperty("resolvedIp").GetString());
        Assert.Equal(stub.Port, ping.GetProperty("resolvedPort").GetInt32());
        Assert.Equal(3, ping.GetProperty("onlinePlayers").GetInt32());
        Assert.Equal(20, ping.GetProperty("maxPlayers").GetInt32());
        Assert.Equal("1.20.1", ping.GetProperty("version").GetString());
        Assert.Equal("A test server", ping.GetProperty("motd").GetString());
        Assert.True(ping.GetProperty("latencyMs").GetInt64() >= 0);
        Assert.True(stub.ReceivedHandshake);
    }

    [Fact]
    public async Task Server_Ping_Failures()
    {
        host = await StartHostWithInstance();

        // nothing to ping -> 404 -------------------------------------------------------
        var missing = await host.Client.PostAsync(
            "/api/v1/instances/servers-inst/servers/0/ping", content: null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // a closed port -> 502 with the uniform error envelope -------------------------
        await host.PostAsync("/api/v1/instances/servers-inst/servers",
            new { name = "Dead", address = "127.0.0.1:1" });
        var dead = await host.Client.PostAsync(
            "/api/v1/instances/servers-inst/servers/0/ping", content: null);
        Assert.Equal(HttpStatusCode.BadGateway, dead.StatusCode);
        Assert.Equal("ping_failed", JsonDocument.Parse(await dead.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>varInt(0x00) + varInt(len) + utf8 json, framed by varInt(packetLength).</summary>
    private static byte[] BuildStatusReply(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var packet = new MemoryStream();
        WriteVarInt(packet, 0x00);
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        var frame = new MemoryStream();
        WriteVarInt(frame, (int)packet.Length);
        frame.Write(packet.ToArray());
        return frame.ToArray();
    }

    internal static void WriteVarInt(MemoryStream stream, int value)
    {
        while ((value & ~0x7F) != 0)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    /// <summary>Accepts one connection at a time, drains the request bytes and replies with
    /// the prebuilt status frame (a second write is served from the same reply).</summary>
    private sealed class PingStubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private bool disposed;

        public PingStubServer(byte[] reply)
        {
            Reply = reply;
            // take over the allocator's already-listening socket: the port is never
            // released and rebound, so the old probe-then-rebind window is gone entirely
            using var lease = PortAllocator.Reserve();
            _listener = lease.TakeListener();
            Port = lease.Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public byte[] Reply { get; }

        public volatile bool ReceivedHandshake;

        private async Task AcceptLoopAsync()
        {
            while (!disposed)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception) when (disposed)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var buffer = new byte[256];
                        await using var stream = client.GetStream();
                        // drain the handshake + status request frames
                        int received;
                        try
                        {
                            do
                            {
                                received = await stream.ReadAsync(buffer);
                            } while (received > 0 && stream.DataAvailable);
                        }
                        catch (IOException)
                        {
                        }
                        ReceivedHandshake = true;
                        await stream.WriteAsync(Reply);
                        await stream.FlushAsync();
                    }
                });
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

[Collection("LauncherJars")]
public class ResourceApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-wave-{Guid.NewGuid():N}");
    private WaveApiHost? host;

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task<WaveApiHost> StartHostWithInstance()
    {
        WaveSeed.Instance(_dataDir, "res-inst", "Res Inst");
        return await WaveApiHost.StartAsync(_dataDir);
    }

    [Fact]
    public async Task Resources_List_Enable_Disable_Delete()
    {
        host = await StartHostWithInstance();
        var gameRoot = host.GameRootOf("res-inst");

        // seed mods (one disabled via the .disabled suffix) + a resource pack
        Directory.CreateDirectory(Path.Combine(gameRoot, "mods"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "mods", "alpha.jar"), "a");
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "mods", "beta.jar.disabled"), "b");
        Directory.CreateDirectory(Path.Combine(gameRoot, "resourcepacks"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "resourcepacks", "pack.zip"), "p");
        Directory.CreateDirectory(Path.Combine(gameRoot, "datapacks"));

        // the merge decision: type=mods on the resources family is rejected in favor of the
        // metadata-rich /mods surface
        var rejected = await host.Client.GetAsync(
            "/api/v1/instances/res-inst/resources/mods");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var mods = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/res-inst/mods")).RootElement;
        Assert.Equal(2, mods.GetArrayLength());
        var alpha = mods.EnumerateArray().Single(m => m.GetProperty("fileName").GetString() == "alpha.jar");
        Assert.True(alpha.GetProperty("enabled").GetBoolean());
        var beta = mods.EnumerateArray().Single(m => m.GetProperty("fileName").GetString() == "beta.jar.disabled");
        Assert.False(beta.GetProperty("enabled").GetBoolean());

        // resourcepacks + datapacks types resolve to their folders --------------------
        var packs = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/res-inst/resources/resourcepacks")).RootElement;
        Assert.Equal(1, packs.GetArrayLength());
        Assert.Equal("pack.zip", packs[0].GetProperty("fileName").GetString());
        var datapacks = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/res-inst/resources/datapacks")).RootElement;
        Assert.Equal(0, datapacks.GetArrayLength());

        // disable: the file gains the .disabled suffix ---------------------------------
        var disable = await host.PostAsync(
            "/api/v1/instances/res-inst/mods/alpha.jar/disable", new { });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.True(File.Exists(Path.Combine(gameRoot, "mods", "alpha.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "mods", "alpha.jar")));

        // enable (by bare name): the suffix is removed ---------------------------------
        var enable = await host.PostAsync(
            "/api/v1/instances/res-inst/mods/beta.jar/enable", new { });
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        Assert.True(File.Exists(Path.Combine(gameRoot, "mods", "beta.jar")));

        // toggling an already-enabled resource conflicts (409, no change) --------------
        var again = await host.PostAsync(
            "/api/v1/instances/res-inst/mods/beta.jar/enable", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("no_change", JsonDocument.Parse(await again.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        // delete by bare name ------------------------------------------------------------
        var delete = await host.Client.DeleteAsync(
            "/api/v1/instances/res-inst/mods/alpha.jar");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(File.Exists(Path.Combine(gameRoot, "mods", "alpha.jar.disabled")));

        // errors: unknown resource 404, unknown type 400 --------------------------------
        var missing = await host.PostAsync(
            "/api/v1/instances/res-inst/mods/ghost.jar/disable", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var unknownType = await host.Client.GetAsync("/api/v1/instances/res-inst/resources/whatnot");
        Assert.Equal(HttpStatusCode.BadRequest, unknownType.StatusCode);
        Assert.Equal("unknown_type", JsonDocument.Parse(await unknownType.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

[Collection("LauncherJars")]
public class ScreenshotApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-wave-{Guid.NewGuid():N}");
    private WaveApiHost? host;

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task<WaveApiHost> StartHostWithScreenshots()
    {
        var root = WaveSeed.Instance(_dataDir, "shots-inst", "Shots Inst");
        var dir = Path.Combine(root, "minecraft", "screenshots");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "b.png"), [0x89, 0x50, 0x4E, 0x47, 4, 5]);
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "not an image");
        return await WaveApiHost.StartAsync(_dataDir);
    }

    [Fact]
    public async Task Screenshots_List_And_Delete()
    {
        host = await StartHostWithScreenshots();

        var shots = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/shots-inst/screenshots")).RootElement;
        Assert.Equal(2, shots.GetArrayLength()); // notes.txt filtered out
        Assert.Equal("a.png", shots[0].GetProperty("name").GetString());
        Assert.Equal(7, shots[0].GetProperty("sizeBytes").GetInt64());
        Assert.True(shots[0].GetProperty("modifiedAt").GetInt64() > 0);

        // delete + 404 on the second attempt -------------------------------------------
        var delete = await host.Client.DeleteAsync("/api/v1/instances/shots-inst/screenshots/a.png");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var again = await host.Client.DeleteAsync("/api/v1/instances/shots-inst/screenshots/a.png");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        // path traversal is refused ------------------------------------------------------
        var escape = await host.Client.DeleteAsync(
            "/api/v1/instances/shots-inst/screenshots/..%2Finstance.cfg");
        Assert.Equal(HttpStatusCode.NotFound, escape.StatusCode);
        Assert.True(File.Exists(Path.Combine(WaveSeed.InstancesDir(_dataDir), "shots-inst", "instance.cfg")));
    }

    [Fact]
    public async Task Screenshots_Upload_Runs_Imgur_Task_Against_Stub()
    {
        string? capturedContentType = null;
        var capturedPaths = new List<string>();
        var capturedBodies = new List<string>();
        var server = new ApiStubServer(async ctx =>
        {
            capturedContentType = ctx.Request.ContentType;
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.Latin1);
            capturedBodies.Add(await reader.ReadToEndAsync());
            capturedPaths.Add(ctx.Request.Url!.AbsolutePath);
            var body = """{"success":true,"data":{"id":"img1","link":"https://imgur.test/img1","deletehash":"hash1"}}""";
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });
        var previousBase = Environment.GetEnvironmentVariable("FLINT_IMGUR_BASE_URL");
        var previousClient = Environment.GetEnvironmentVariable("FLINT_IMGUR_CLIENT_ID");
        Environment.SetEnvironmentVariable("FLINT_IMGUR_BASE_URL", server.Url + "/");
        Environment.SetEnvironmentVariable("FLINT_IMGUR_CLIENT_ID", "wave-client");
        try
        {
            host = await StartHostWithScreenshots();

            // one screenshot: a single image upload, no album request
            var upload = await host.PostAsync("/api/v1/instances/shots-inst/screenshots/upload",
                new { names = new[] { "b.png" } });
            Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
            var taskId = JsonDocument.Parse(await upload.Content.ReadAsStringAsync())
                .RootElement.GetProperty("taskId").GetString()!;
            await host.WaitForTaskAsync(taskId, TimeSpan.FromSeconds(20));

            Assert.Single(capturedBodies);
            Assert.Equal("/image", capturedPaths[0]);
            Assert.Contains("multipart/form-data", capturedContentType);
            Assert.Contains("image/png", capturedBodies[0]);
            Assert.Contains("filename=\"b.png\"", capturedBodies[0]);

            // unknown names are validated before any task runs --------------------------
            var multi = await host.PostAsync("/api/v1/instances/shots-inst/screenshots/upload",
                new { names = new[] { "a.png", "b.png", "missing.png" } });
            Assert.Equal(HttpStatusCode.NotFound, multi.StatusCode);
            Assert.Single(capturedBodies); // nothing uploaded

            // two screenshots: 2 image uploads + the album creation call ----------------
            var ok = await host.PostAsync("/api/v1/instances/shots-inst/screenshots/upload",
                new { names = new[] { "a.png", "b.png" } });
            ok.EnsureSuccessStatusCode();
            var multiTaskId = JsonDocument.Parse(await ok.Content.ReadAsStringAsync())
                .RootElement.GetProperty("taskId").GetString()!;
            await host.WaitForTaskAsync(multiTaskId, TimeSpan.FromSeconds(20));
            Assert.Equal(4, capturedBodies.Count); // 1 + 2 images + 1 album
            Assert.Equal(3, capturedPaths.Count(path => path == "/image"));
            Assert.Equal(1, capturedPaths.Count(path => path == "/album"));
            Assert.Contains("deletehashes=", capturedBodies[^1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLINT_IMGUR_BASE_URL", previousBase);
            Environment.SetEnvironmentVariable("FLINT_IMGUR_CLIENT_ID", previousClient);
            server.Dispose();
        }
    }
}

[Collection("LauncherJars")]
public class GameOptionsApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-wave-{Guid.NewGuid():N}");
    private WaveApiHost? host;

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task GameOptions_Put_Get_RoundTrip()
    {
        WaveSeed.Instance(_dataDir, "opts-inst", "Opts Inst");
        host = await WaveApiHost.StartAsync(_dataDir);

        var put = await host.PutAsync("/api/v1/instances/opts-inst/gameoptions",
            new { options = new[] { new { key = "difficulty", value = "hard" },
                                    new { key = "renderDistance", value = "12" } } });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // the file itself is the options.txt of the game root
        var text = await File.ReadAllTextAsync(
            Path.Combine(host.GameRootOf("opts-inst"), "options.txt"));
        Assert.Contains("difficulty:hard", text);
        Assert.Contains("renderDistance:12", text);

        // GET reads the parsed map
        var options = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/opts-inst/gameoptions")).RootElement;
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("difficulty", options[0].GetProperty("key").GetString());
        Assert.Equal("hard", options[0].GetProperty("value").GetString());

        // a second PUT updates an existing key in place
        put = await host.PutAsync("/api/v1/instances/opts-inst/gameoptions",
            new { options = new[] { new { key = "difficulty", value = "peaceful" } } });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        options = JsonDocument.Parse(await host.Client.GetStringAsync(
            "/api/v1/instances/opts-inst/gameoptions")).RootElement;
        Assert.Equal(2, options.GetArrayLength());
        Assert.Contains(options.EnumerateArray(),
            o => o.GetProperty("key").GetString() == "difficulty" &&
                 o.GetProperty("value").GetString() == "peaceful");

        // validation: empty body + unknown instance -----------------------------------
        var bad = await host.PutAsync("/api/v1/instances/opts-inst/gameoptions",
            new { options = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await host.Client.GetAsync("/api/v1/instances/ghost/gameoptions");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
