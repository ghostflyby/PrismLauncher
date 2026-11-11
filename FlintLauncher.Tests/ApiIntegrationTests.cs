// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// API integration tests: host the real FlintLauncher.Web app in-process
// (WebHostBuilder.BuildWebApplication with --data-dir <temp> + a random port) and drive the
// whole HTTP surface with HttpClient.
//
// Collections (process-global statics make these tests unparallelizable with the suites
// that own the hooks):
//  - the main class joins "LauncherJars" — the e2e launch test points the host's install
//    root at the jars layout exactly like RealLaunchSmokeTests/LauncherPartLaunchTests;
//  - the Modrinth search class joins "ModrinthApi" (the client Options.BaseUrl hook).
// The device-code class needs no collection anymore: it redirects the endpoints through a
// stub-pointed AuthEndpointsOptions handed to its own host.
// Environment.CurrentDirectory is process-global too: every host sets it (core paths are
// CWD-relative) and restores it on dispose.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.State;
using FlintLauncher.ExternalApi.AtLauncher;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Ftb;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.ExternalApi.Technic;
using FlintLauncher.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

// --------------------------------------------------------------------------- host fixture

internal sealed class FlintApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _previousCwd;

    private FlintApiHost(WebApplication app, HttpClient client, string homeDir, string dataDir,
        string previousCwd, string token)
    {
        _app = app;
        Client = client;
        HomeDir = homeDir;
        DataDir = dataDir;
        Token = token;
        _previousCwd = previousCwd;
    }

    public HttpClient Client { get; }

    /// <summary>The host's service provider — the registered singletons (the network stack,
    /// the stores) a test asserts against directly.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The temp home root (passed as --home-dir; cleaned up on dispose).</summary>
    public string HomeDir { get; }

    /// <summary>The data root (home layout: &lt;home&gt;/data) — where instances, accounts
    /// and the API token live.</summary>
    public string DataDir { get; }

    public static async Task<FlintApiHost> StartAsync(ModrinthClient? modrinth = null, FlameClient? flame = null,
        TechnicClient? technic = null, AtLauncherClient? atlauncher = null, FtbClient? ftb = null,
        AuthEndpointsOptions? auth = null, IReadOnlyList<AbsolutePath>? migrationSourceParents = null)
    {
        var homeDir = Path.Combine(Path.GetTempPath(), $"flint-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(homeDir);
        var dataDir = Path.Combine(homeDir, "data");
        Directory.CreateDirectory(dataDir);
        var previousCwd = Environment.CurrentDirectory;
        // the token is host-injected (--api-token) — there is no token file to read
        var token = Guid.NewGuid().ToString("N");
        var app = WebHostBuilder.BuildWebApplication(
        [
            "--home-dir", homeDir,
            "--api-token", token,
            "--urls", "http://127.0.0.1:0",
        ], modrinth, flame, technic, atlauncher, ftb, auth, migrationSourceParents: migrationSourceParents);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        // every API route (except /healthz and /api/v1/info) requires the injected token
        client.DefaultRequestHeaders.Add("X-Api-Token", token);
        return new FlintApiHost(app, client, homeDir, dataDir, previousCwd, token);
    }

    /// <summary>A client WITHOUT credentials, for auth-rejection assertions.</summary>
    public HttpClient CreateUnauthorizedClient()
    {
        var baseAddress = Client.BaseAddress;
        Assert.NotNull(baseAddress);
        return new HttpClient { BaseAddress = new Uri(baseAddress.ToString()) };
    }

    public string Token { get; }

    public async Task<HttpResponseMessage> GetAsync(string path) => await Client.GetAsync(path);

    public async Task<HttpResponseMessage> PostAsync<T>(string path, T body)
    {
        var response = await Client.PostAsJsonAsync(path, body);
        return response;
    }

    public async Task<HttpResponseMessage> PutAsync<T>(string path, T body) =>
        await Client.PutAsJsonAsync(path, body);

    public async Task<HttpResponseMessage> PatchAsync<T>(string path, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> DeleteAsync(string path) => Client.DeleteAsync(path);

    /// <summary>Poll GET /api/v1/tasks/{id} until the state matches (or timeout -> fail).</summary>
    public async Task<JsonElement> WaitForTaskStateAsync(string taskId, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement task = default;
        while (DateTime.UtcNow < deadline)
        {
            var response = await GetAsync($"/api/v1/tasks/{taskId}");
            response.EnsureSuccessStatusCode();
            task = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            if (task.GetProperty("state").GetString() == expected)
            {
                return task;
            }

            await Task.Delay(100);
        }

        throw new Exception($"task {taskId} never reached '{expected}', last state: {task.GetRawText()}");
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
            // previousCwd may have been another (parallel) host's data dir that is already
            // gone — fall back to a directory that always exists
            var restoreTo = Directory.Exists(_previousCwd) ? _previousCwd : Path.GetTempPath();
            Environment.CurrentDirectory = restoreTo;
            try
            {
                Directory.Delete(HomeDir, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}

/// <summary>Minimal stub JSON server (device-code endpoints, modrinth search).</summary>
internal sealed class ApiStubServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;
    private bool _disposed;

    public ApiStubServer(Func<HttpListenerContext, Task> handler)
    {
        Handler = handler;
        // TestPorts owns the port lease AND the rebind-retry (a blind random pick could
        // collide with a peer test class or a foreign process; HttpListener cannot adopt the
        // lease socket, so losing the takeover window retries on a fresh port)
        (_listener, Url) = PortAllocator.StartHttpListener();
        _loop = Task.Run(ListenLoop);
    }

    private Func<HttpListenerContext, Task> Handler { get; }

    public string Url { get; }

    private async Task ListenLoop()
    {
        while (!_disposed)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Handler(context);
                }
                catch
                {
                    // stub: swallow handler faults
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException) { }

        GC.KeepAlive(_loop);
    }
}

// ---------------------------------------------------------------------------------- tests

[Collection("LauncherJars")]
public class FlintApiIntegrationTests
{
    [Fact]
    public async Task Info_And_Health_Expose_Config()
    {
        await using var host = await FlintApiHost.StartAsync();

        var health = await host.GetAsync("/healthz");
        health.EnsureSuccessStatusCode();
        Assert.Equal("ok", JsonDocument.Parse(await health.Content.ReadAsStringAsync())
            .RootElement.GetProperty("status").GetString());

        var info = await host.GetAsync("/api/v1/info");
        info.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await info.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Flint Launcher", json.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrEmpty(json.GetProperty("userAgent").GetString()));
    }

    [Fact]
    public async Task Instance_RoundTrip_Create_List_Get_Patch_Delete()
    {
        await using var host = await FlintApiHost.StartAsync();

        // create (201 + id) ------------------------------------------------------------
        var create = await host.PostAsync("/api/v1/instances/vanilla",
            new { name = "Round Trip", mcVersion = "1.20.1", iconKey = "flint", group = "Packs" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.False(string.IsNullOrEmpty(id));

        // list -------------------------------------------------------------------------
        var list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        var entry = list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
        Assert.Equal("Round Trip", entry.GetProperty("name").GetString());
        Assert.Equal("flint", entry.GetProperty("iconKey").GetString());
        Assert.Equal("Packs", entry.GetProperty("group").GetString());
        Assert.Equal(0L, entry.GetProperty("lastLaunchTime").GetInt64());
        Assert.False(entry.GetProperty("running").GetBoolean());
        // the wire shape: every handle of the addressing chain is exposed
        Assert.Equal(id, entry.GetProperty("dirName").GetString());
        var uuid = entry.GetProperty("uuid").GetString();
        Assert.NotNull(uuid);
        Assert.False(string.IsNullOrEmpty(uuid));
        // unix stat; windows has no inode and reports 0
        if (!OperatingSystem.IsWindows())
        {
            Assert.NotEqual(0L, entry.GetProperty("inode").GetInt64());
        }

        // detail -----------------------------------------------------------------------
        var detail = JsonDocument.Parse(
            await (await host.GetAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}")).Content
                .ReadAsStringAsync()).RootElement;
        Assert.Equal("OneSix", detail.GetProperty("instanceType").GetString());
        Assert.Equal("", detail.GetProperty("notes").GetString());
        Assert.EndsWith("minecraft", detail.GetProperty("gameRoot").GetString());

        // patch notes + icon -----------------------------------------------------------
        var patch = await host.PatchAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}",
            new { notes = "hello api", iconKey = "infinity" });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        detail = JsonDocument.Parse(
            await (await host.GetAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}")).Content
                .ReadAsStringAsync()).RootElement;
        Assert.Equal("hello api", detail.GetProperty("notes").GetString());
        Assert.Equal("infinity", detail.GetProperty("iconKey").GetString());

        // rename: a pure METADATA write — the directory (and with it the id)
        // stays, the name changes, and the uuid holds as the stable reference -----------
        var rename = await host.PatchAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}",
            new { name = "Renamed Trip" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
        entry = list.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Renamed Trip");
        Assert.Equal("Renamed Trip", entry.GetProperty("name").GetString());
        // the dirname handle did not move and the uuid survived
        Assert.Equal(id, entry.GetProperty("id").GetString());
        Assert.Equal(uuid, entry.GetProperty("uuid").GetString());
        Assert.Equal(id, entry.GetProperty("dirName").GetString());

        // the same instance resolves through uuid AND dirname --------------
        var byUuid = JsonDocument.Parse(
            await (await host.GetAsync($"/api/v1/instances/{Uri.EscapeDataString(uuid)}")).Content
                .ReadAsStringAsync()).RootElement;
        Assert.Equal("Renamed Trip", byUuid.GetProperty("name").GetString());
        var byDirname = JsonDocument.Parse(
            await (await host.GetAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}")).Content
                .ReadAsStringAsync()).RootElement;
        Assert.Equal(uuid, byDirname.GetProperty("uuid").GetString());

        // patch the group on the renamed instance --------------------------------------
        Assert.Equal(HttpStatusCode.NoContent, (
            await host.PatchAsync(
                $"/api/v1/instances/{Uri.EscapeDataString(id)}", new { group = "Moved" })).StatusCode);
        Assert.Equal("Moved", JsonDocument.Parse(
            await (await host.GetAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}")).Content
                .ReadAsStringAsync()).RootElement.GetProperty("group").GetString());

        // delete + 404 -----------------------------------------------------------------
        Assert.Equal(HttpStatusCode.NoContent,
            (await host.DeleteAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}")).StatusCode);
        var missing = await host.GetAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var error = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("instance_not_found", error.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("error").GetProperty("message").GetString()));

        // validation errors ------------------------------------------------------------
        var invalid = await host.PostAsync("/api/v1/instances/vanilla", new { name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_body", JsonDocument.Parse(await invalid.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Instance_Settings_Put_Get_Typed_Map()
    {
        await using var host = await FlintApiHost.StartAsync();

        var create = await host.PostAsync("/api/v1/instances/vanilla",
            new { name = "Settings Inst", mcVersion = "1.20.1" });
        create.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();
        Assert.NotNull(id);

        // PUT with kind-carrying values -------------------------------------------------
        // the gate has no boolean anymore — JavaPath at the instance layer IS the
        // override, and the derived OverrideJavaLocation gate reads true
        Assert.Equal(HttpStatusCode.NoContent, (
            await host.PutAsync(
                $"/api/v1/instances/{Uri.EscapeDataString(id)}/settings",
                new
                {
                    overrides = new Dictionary<string, object>
                    {
                        ["OverrideJavaLocation"] = true,
                        ["JavaPath"] = "/tmp/flint-e2e-java",
                        ["JoinServerOnLaunch"] = true,
                        ["notes"] = "typed",
                        ["iconKey"] = "grass",
                    },
                })).StatusCode);

        var settings = JsonDocument.Parse(await (await host.GetAsync(
                $"/api/v1/instances/{Uri.EscapeDataString(id)}/settings")).Content.ReadAsStringAsync())
            .RootElement;
        // the settings map exposes the derived gates, the instance-layer members and
        // the runtime time trio
        Assert.Equal(JsonValueKind.True, settings.GetProperty("OverrideJavaLocation").ValueKind);
        Assert.Equal("/tmp/flint-e2e-java", settings.GetProperty("JavaPath").GetString());
        Assert.Equal(JsonValueKind.True, settings.GetProperty("JoinServerOnLaunch").ValueKind);
        Assert.Equal("typed", settings.GetProperty("notes").GetString());
        Assert.Equal("grass", settings.GetProperty("iconKey").GetString());

        // unknown key -> 400 with the uniform error shape ------------------------------
        var unknown = await host.PutAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/settings",
            new { overrides = new Dictionary<string, object> { ["NotASetting"] = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("unknown_setting", JsonDocument.Parse(await unknown.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        // unknown instance -> 404 ------------------------------------------------------
        var missing = await host.PutAsync("/api/v1/instances/ghost/settings",
            new { overrides = new Dictionary<string, object> { ["notes"] = "x" } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Accounts_Offline_RoundTrip()
    {
        await using var host = await FlintApiHost.StartAsync();

        var create = await host.PostAsync("/api/v1/accounts/offline", new { name = "ApiHero" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var account = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement;
        var profileId = account.GetProperty("profileId").GetString();
        Assert.NotNull(profileId);
        Assert.Equal("ApiHero", account.GetProperty("name").GetString());
        Assert.Equal("offline", account.GetProperty("type").GetString());
        Assert.False(account.GetProperty("isDefault").GetBoolean());

        var list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/accounts")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(profileId, list[0].GetProperty("profileId").GetString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await host.PutAsync($"/api/v1/accounts/{profileId}/default", new { })).StatusCode);
        list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/accounts")).Content.ReadAsStringAsync()).RootElement;
        Assert.True(list[0].GetProperty("isDefault").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent,
            (await host.DeleteAsync($"/api/v1/accounts/{profileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.DeleteAsync($"/api/v1/accounts/{profileId}")).StatusCode);
        list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/accounts")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Import_Local_MultiMC_Zip_Runs_As_Tracked_Task()
    {
        await using var host = await FlintApiHost.StartAsync();

        // a minimal MultiMC-flavoured zip: one folder with an instance.cfg inside
        var zipPath = Path.Combine(host.DataDir, "pack.zip");
        using (var zip = await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("pack/instance.cfg");
            await using var writer = new StreamWriter(await entry.OpenAsync());
            await writer.WriteAsync("name=Pack Inst\n");
        }

        var post = await host.PostAsync("/api/v1/instances/import",
            new { url = new Uri(zipPath).ToString(), name = "Imported Pack" });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var taskId = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString();
        Assert.NotNull(taskId);

        var task = await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(20));
        Assert.Equal("import", task.GetProperty("kind").GetString());
        // the creation's own narration reaches the task view (the wrap forwards its events)
        Assert.False(string.IsNullOrEmpty(task.GetProperty("status").GetString()),
            "the import task should carry the creation's last status line");

        // the committed instance is discoverable and carries the pack name
        var list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
        var imported = list.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Imported Pack");
        var importedId = imported.GetProperty("id").GetString();
        Assert.NotNull(importedId);

        // tasks listing shows the finished import
        var tasks = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/tasks")).Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("id").GetString() == taskId);

        // and it can be deleted again through the API
        Assert.Equal(HttpStatusCode.NoContent,
            (await host.DeleteAsync($"/api/v1/instances/{Uri.EscapeDataString(importedId)}")).StatusCode);

        // invalid URL -> 400 -----------------------------------------------------------
        var invalid = await host.PostAsync("/api/v1/instances/import", new { url = "not-a-url" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Tasks_Unknown_And_Abort_Errors()
    {
        await using var host = await FlintApiHost.StartAsync();

        var missing = await host.GetAsync("/api/v1/tasks/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("task_not_found", JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        var abort = await host.PostAsync("/api/v1/tasks/nope/abort", new { });
        Assert.Equal(HttpStatusCode.NotFound, abort.StatusCode);

        var proceed = await host.PostAsync("/api/v1/tasks/nope/proceed", new { });
        Assert.Equal(HttpStatusCode.NotFound, proceed.StatusCode);
    }

    [Fact]
    public async Task News_Endpoint_Is_Retired()
    {
        // the news surface is deleted whole — checker, endpoint, feed base, env
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------- full launch e2e

    private static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string? LauncherJarsRoot()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var launcherRoot = Path.Combine(repoRoot, "build");
        return File.Exists(Path.Combine(launcherRoot, "jars", "NewLaunch.jar"))
            ? launcherRoot
            : null;
    }

    [Fact]
    public async Task Launch_Through_Api_EndToEnd_With_FakeJava()
    {
        if (!IsUnix || LauncherJarsRoot() is not { } launcherRoot)
        {
            return; // needs the unix fake-java + the built NewLaunch.jar
        }

        var dataDir = Path.Combine(Path.GetTempPath(), $"flint-api-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var dataRoot = Path.Combine(dataDir, "data"); // the home layout's data root
        var previousCwd = Environment.CurrentDirectory;

        // seed an instance on disk the same way the LaunchFixture does (patched net.minecraft
        // profile loads fully offline) — the API discovers it at startup
        var instanceRoot = Path.Combine(dataRoot, "instances", "e2e-inst");
        Directory.CreateDirectory(instanceRoot);
        await File.WriteAllTextAsync(Path.Combine(instanceRoot, "instance.cfg"), "name=E2E Instance\n");
        Directory.CreateDirectory(Path.Combine(instanceRoot, "patches"));
        await File.WriteAllTextAsync(Path.Combine(instanceRoot, "patches", "net.minecraft.json"),
            LaunchFixture.DefaultPatchJson);
        await File.WriteAllTextAsync(Path.Combine(instanceRoot, "mmc-pack.json"),
            """{"formatVersion":1,"components":[{"important":true,"uid":"net.minecraft"}]}""");
        // the migration pass a cfg-only directory goes through before the host lists it
        TestLegacyConversion.ConvertOne(new AbsolutePath(instanceRoot));

        var root = Path.Combine(dataDir, "scratch");
        Directory.CreateDirectory(root);
        var javaPath = WriteFakeJava(root);
        var javaUnixTime = new DateTimeOffset(File.GetLastWriteTimeUtc(javaPath)).ToUnixTimeMilliseconds();

        try
        {
            // the token is host-injected (--api-token) — no token file exists to read
            var apiToken = Guid.NewGuid().ToString("N");
            var app = WebHostBuilder.BuildWebApplication(
                [
                    "--home-dir", dataDir,
                    "--api-token", apiToken,
                    "--urls", "http://127.0.0.1:0",
                ],
                // the launch pipeline's jar lookups resolve against this install root (the former
                // JavaUtils.LauncherRootPath static test hook, now the LaunchFactory paths's InstallRoot)
                paths: FlintPaths.FromHomeDir(new AbsolutePath(dataDir)) with { InstallRoot = launcherRoot });
            await app.StartAsync();
            using var client = new HttpClient();
            client.BaseAddress = new Uri(app.Urls.First());
            client.DefaultRequestHeaders.Add("X-Api-Token", apiToken);
            Environment.CurrentDirectory = dataDir;

            // seed the java probe cache in the launcher state (the former settings-key
            // seeding — the probe quintet moved out of the settings world) so
            // CheckJava trusts the fake java without running the checker
            app.Services.GetRequiredService<LauncherStateStore>().Update(state => state with
            {
                Java = state.Java with
                {
                    Detections = new Dictionary<string, JavaDetectEntry>(
                        state.Java.Detections, StringComparer.Ordinal)
                    {
                        [javaPath] = new(CheckJava.ComputeJavaSignature(javaUnixTime, javaPath),
                            "64", "arm64", "17.0.2", "api-e2e", 17),
                    },
                },
            });

            // instance visible with its display name -----------------------------------
            var list = JsonDocument.Parse(
                await client.GetStringAsync("/api/v1/instances")).RootElement;
            Assert.Equal("e2e-inst", list[0].GetProperty("id").GetString());

            // point the instance at the fake java through the settings API -------------
            var put = await client.PutAsJsonAsync("/api/v1/instances/e2e-inst/settings", new
            {
                overrides = new Dictionary<string, object>
                {
                    ["OverrideJavaLocation"] = true,
                    ["JavaPath"] = javaPath,
                },
            });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            // launch offline: 202 + task id --------------------------------------------
            var launch = await client.PostAsJsonAsync("/api/v1/instances/e2e-inst/launch",
                new { mode = "offline", offlineName = "ApiTester" });
            Assert.Equal(HttpStatusCode.Accepted, launch.StatusCode);
            var taskId = JsonDocument.Parse(await launch.Content.ReadAsStringAsync())
                .RootElement.GetProperty("taskId").GetString();
            Assert.NotNull(taskId);

            // the same instance cannot launch twice (core CanLaunch) --------------------
            var second = await client.PostAsJsonAsync("/api/v1/instances/e2e-inst/launch",
                new { mode = "offline", offlineName = "ApiTester" });
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("instance_unavailable", JsonDocument.Parse(
                    await second.Content.ReadAsStringAsync())
                .RootElement.GetProperty("error").GetProperty("code").GetString());

            // AutoProceed is false for API launches: the task parks once per gate — the
            // instance-update progress gate first, then the launch-confirmation gate
            var updateGate = await PollTaskState(client, taskId, "waiting", TimeSpan.FromSeconds(30));
            Assert.Equal("launch", updateGate.GetProperty("kind").GetString());
            var proceed = await client.PostAsJsonAsync($"/api/v1/tasks/{taskId}/proceed", new { });
            proceed.EnsureSuccessStatusCode();

            var launchGate = await PollTaskState(client, taskId, "waiting", TimeSpan.FromSeconds(30));
            Assert.Equal("launch", launchGate.GetProperty("kind").GetString());
            var proceed2 = await client.PostAsJsonAsync($"/api/v1/tasks/{taskId}/proceed", new { });
            proceed2.EnsureSuccessStatusCode();

            var done = await PollTaskState(client, taskId, "succeeded", TimeSpan.FromSeconds(30));

            // a duplicate proceed is an explicit 409: LaunchTask.Proceed() no-ops outside
            // Waiting, so the endpoint surfaces it instead of silently dropping the call
            var dupe = await client.PostAsJsonAsync($"/api/v1/tasks/{taskId}/proceed", new { });
            Assert.Equal(HttpStatusCode.Conflict, dupe.StatusCode);
            Assert.Equal("launch_not_waiting", JsonDocument.Parse(
                    await dupe.Content.ReadAsStringAsync())
                .RootElement.GetProperty("error").GetProperty("code").GetString());

            // the launch script reached the fake EntryPoint and came back via the log --
            var log = JsonDocument.Parse(
                await client.GetStringAsync($"/api/v1/tasks/{taskId}/log?since=0")).RootElement;
            var lines = log.GetProperty("lines").EnumerateArray().ToList();
            Assert.Contains(lines, l => l.GetProperty("line").GetString() == "userName ApiTester");
            Assert.Contains(lines, l => l.GetProperty("line").GetString() == "param --username");
            Assert.Contains(lines, l => l.GetProperty("line").GetString() == "param ApiTester");
            Assert.Contains(lines, l => l.GetProperty("level").GetString() == "launcher");
            // `since` filtering returns only later lines
            var last = lines[^1].GetProperty("index").GetInt32();
            var tail = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/v1/tasks/{taskId}/log?since={last}")).RootElement;
            Assert.Equal(0, tail.GetProperty("lines").GetArrayLength());

            // game-time bookkeeping happened --------------------------------------------
            var detail = JsonDocument.Parse(
                await client.GetStringAsync("/api/v1/instances/e2e-inst")).RootElement;
            Assert.True(detail.GetProperty("lastLaunchTime").GetInt64() > 0);
            Assert.False(detail.GetProperty("running").GetBoolean());

            // SSE stream: replays the buffered log and ends with the terminal state -----
            using (var stream = await client.GetAsync($"/api/v1/tasks/{taskId}/stream",
                       HttpCompletionOption.ResponseHeadersRead))
            {
                stream.EnsureSuccessStatusCode();
                var contentType = stream.Content.Headers.ContentType;
                Assert.NotNull(contentType);
                Assert.Contains("text/event-stream", contentType.ToString());
                var text = await stream.Content.ReadAsStringAsync();
                Assert.Contains("data: ", text);
                Assert.Contains("userName ApiTester", text);
                Assert.Contains("event: state", text);
                Assert.Contains("\"state\":\"succeeded\"", text);
            }

            _ = done;
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<JsonElement> PollTaskState(HttpClient client, string taskId,
        string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement task = default;
        while (DateTime.UtcNow < deadline)
        {
            task = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/tasks/{taskId}")).RootElement
                .Clone();
            var state = task.GetProperty("state").GetString();
            if (state == expected)
            {
                return task;
            }

            if (state is "failed" or "aborted")
            {
                throw new Exception($"launch task ended in '{state}': {task.GetProperty("failReason").GetString()}");
            }

            await Task.Delay(100);
        }

        throw new Exception($"task never reached '{expected}', last: {task.GetRawText()}");
    }

    private static string WriteFakeJava(string root)
    {
        var path = Path.Combine(root, "fake-java");
        File.WriteAllText(path, """
                                #!/bin/sh
                                exec 2>>/tmp/fj-stderr.log
                                while IFS= read -r line || [ -n "$line" ]; do
                                    printf '%s\n' "$line"
                                    if [ "$line" = "launch" ]; then
                                        exit 0
                                    fi
                                    if [ "$line" = "abort" ]; then
                                        exit 42
                                    fi
                                done
                                exit 1
                                """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return path;
    }
}

// ------------------------------------------------------- MSA device-code endpoints

public class FlintApiDeviceCodeTests : IDisposable
{
    private readonly ApiStubServer _server;
    private readonly AuthEndpointsOptions _auth = new();

    private const string MsaDeviceCode = """
                                         {"device_code":"dev123","user_code":"FLNT-CODE","verification_uri":"https://microsoft.test/link","expires_in":900,"interval":1}
                                         """;

    private string _msaToken;

    public FlintApiDeviceCodeTests()
    {
        // token endpoint answer is switchable per phase of the test
        _msaToken = """{"error":"authorization_pending"}""";
        _server = new ApiStubServer(async ctx =>
        {
            var url = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
            var body = url.AbsolutePath.EndsWith("/devicecode", StringComparison.Ordinal)
                ? MsaDeviceCode
                : _msaToken;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        _auth.Mojang.Options.MsaDeviceCode = _server.Url + "/devicecode";
        _auth.Mojang.Options.MsaToken = _server.Url + "/token";
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task DeviceCode_Start_Poll_Pending_Expired()
    {
        await using var host = await FlintApiHost.StartAsync(auth: _auth);

        // start: 202 with the code triplet and an internal correlation id
        var start = await host.PostAsync("/api/v1/accounts/msa/devicecode", new { });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var started = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("FLNT-CODE", started.GetProperty("userCode").GetString());
        Assert.Equal("https://microsoft.test/link", started.GetProperty("verificationUri").GetString());
        Assert.Equal(900, started.GetProperty("expiresInSeconds").GetInt32());
        var deviceId = started.GetProperty("deviceId").GetString();
        Assert.NotNull(deviceId);
        Assert.False(string.IsNullOrEmpty(deviceId));

        // unknown device -> 404
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.PostAsync("/api/v1/accounts/msa/devicecode/unknown/poll", new { })).StatusCode);

        // pending while the user has not authorized
        var poll = JsonDocument.Parse(await (await host.PostAsync(
                $"/api/v1/accounts/msa/devicecode/{deviceId}/poll", new { })).Content.ReadAsStringAsync())
            .RootElement;
        Assert.Equal("pending", poll.GetProperty("state").GetString());
        Assert.True(poll.GetProperty("account").ValueKind is JsonValueKind.Null);

        // deny the code server-side: the flow fails -> "expired"
        _msaToken = """{"error":"authorization_expired"}""";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var state = "pending";
        while (state == "pending" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var next = await host.PostAsync($"/api/v1/accounts/msa/devicecode/{deviceId}/poll", new { });
            next.EnsureSuccessStatusCode();
            var nextState = JsonDocument.Parse(await next.Content.ReadAsStringAsync())
                .RootElement.GetProperty("state").GetString();
            Assert.NotNull(nextState);
            state = nextState;
        }

        Assert.Equal("expired", state);
    }
}

// ------------------------------------------------------ Modrinth search endpoint

[Collection("ModrinthApi")]
public class FlintApiModrinthSearchTests : IDisposable
{
    private readonly ApiStubServer _server = new(ServeSearch);

    private static string HitsJson =>
        """{"hits":[{"project_id":"proj1","slug":"jei","title":"Just Enough Items","icon_url":"https://cdn.test/jei.png","author":"mezz","downloads":12345}]}""";

    public static string? LastSearchQuery { get; private set; }

    private static async Task ServeSearch(HttpListenerContext ctx)
    {
        var url = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
        LastSearchQuery = url.Query;
        var bytes = Encoding.UTF8.GetBytes(HitsJson);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private readonly ModrinthClient _modrinth;

    public FlintApiModrinthSearchTests()
    {
        _modrinth = TestModrinthApi.At(_server.Url).Client;
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task Modrinth_Search_Passes_Through_Core_Api()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinth);

        var response = await host.GetAsync(
            "/api/v1/modplatform/modrinth/search?query=jei&version=1.20.1&loader=fabric&index=downloads&offset=40");
        response.EnsureSuccessStatusCode();

        // the wire query: page position, free text, sort index, and the facet groups (each
        // group OR-s its values, the groups AND together)
        var query = Uri.UnescapeDataString(LastSearchQuery ?? "");
        Assert.Contains("offset=40", query);
        Assert.Contains("limit=25", query);
        Assert.Contains("query=jei", query);
        Assert.Contains("index=downloads", query);
        var facets = JsonDocument.Parse(query[(query.IndexOf("facets=", StringComparison.Ordinal) + "facets=".Length)..]).RootElement;
        var groups = facets.EnumerateArray()
            .Select(g => g.EnumerateArray().Select(v => v.GetString()).ToList()).ToList();
        Assert.Contains(groups, g => g.SequenceEqual(["categories:fabric"]));
        Assert.Contains(groups, g => g.SequenceEqual(["versions:1.20.1"]));
        Assert.Contains(groups, g => g.SequenceEqual(["project_type:mod"]));

        var hits = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, hits.GetArrayLength());
        Assert.Equal("proj1", hits[0].GetProperty("id").GetString());
        Assert.Equal("jei", hits[0].GetProperty("slug").GetString());
        Assert.Equal("Just Enough Items", hits[0].GetProperty("title").GetString());
        Assert.Equal("https://cdn.test/jei.png", hits[0].GetProperty("iconUrl").GetString());
        Assert.Equal("mezz", hits[0].GetProperty("author").GetString());
        Assert.Equal(12345L, hits[0].GetProperty("downloads").GetInt64());
    }

    [Fact]
    public async Task Modrinth_Search_Rejects_Unsupported_Loaders_With_The_Error_Envelope()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinth);

        var response = await host.GetAsync("/api/v1/modplatform/modrinth/search?loader=unknown-thing");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_loader", body.GetProperty("error").GetProperty("code").GetString());
    }
}

// ------------------------------------------------------- API token auth

[Collection("LauncherJars")]
public class ApiAuthTests
{
    [Fact]
    public async Task Requests_Without_Token_Are_Rejected_With_The_Error_Envelope()
    {
        await using var host = await FlintApiHost.StartAsync();
        using var client = host.CreateUnauthorizedClient();

        var response = await client.GetAsync("/api/v1/instances");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("unauthorized", body.GetProperty("error").GetProperty("code").GetString());

        // info + healthz stay open for discovery
        Assert.True((await host.CreateUnauthorizedClient().GetAsync("/healthz")).IsSuccessStatusCode);
        Assert.True((await host.CreateUnauthorizedClient().GetAsync("/api/v1/info")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Wrong_Token_Is_Rejected_Right_Token_Is_Accepted_On_Both_Header_Forms()
    {
        await using var host = await FlintApiHost.StartAsync();

        using var wrong = host.CreateUnauthorizedClient();
        wrong.DefaultRequestHeaders.Add("Authorization", "Bearer nope");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await wrong.GetAsync("/api/v1/instances")).StatusCode);

        using var bearer = host.CreateUnauthorizedClient();
        bearer.DefaultRequestHeaders.Add("Authorization", $"Bearer {host.Token}");
        Assert.True((await bearer.GetAsync("/api/v1/instances")).IsSuccessStatusCode);

        using var headerToken = host.CreateUnauthorizedClient();
        headerToken.DefaultRequestHeaders.Add("X-Api-Token", host.Token);
        Assert.True((await headerToken.GetAsync("/api/v1/instances")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task NoAuth_Flag_Disables_The_Gate()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"flint-api-noauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previousCwd = Environment.CurrentDirectory;
        try
        {
            var app = WebHostBuilder.BuildWebApplication(
            [
                "--home-dir", dataDir,
                "--urls", "http://127.0.0.1:0",
                "--no-auth",
            ]);
            await app.StartAsync();
            // one-shot request against the hosted stub; a single throwaway client here is
            // the honest shape (IHttpClientFactory is not registered in the test host)
            // ReSharper disable once ShortLivedHttpClient
            using var client = new HttpClient();
            client.BaseAddress = new Uri(app.Urls.First());
            Assert.True((await client.GetAsync("/api/v1/instances")).IsSuccessStatusCode);
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            await File.WriteAllTextAsync(Path.Combine(dataDir, "disposed"), "");
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
