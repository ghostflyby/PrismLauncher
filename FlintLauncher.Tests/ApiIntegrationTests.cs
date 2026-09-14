// SPDX-License-Identifier: GPL-3.0-only
// P7-b API integration tests: host the real FlintLauncher.Web app in-process
// (WebHostBuilder.BuildWebApplication with --data-dir <temp> + a random port) and drive the
// whole HTTP surface with HttpClient.
//
// Collections (process-global statics make these tests unparallelizable with the suites
// that own the hooks):
//  - the main class joins "LauncherJars" — the e2e launch test mutates
//    JavaUtils.LauncherRootPath exactly like RealLaunchSmokeTests/LauncherPartLaunchTests;
//  - the device-code class joins "AuthEndpoints" (AuthEndpoints.* are static hooks);
//  - the Modrinth search class joins "ModrinthApi" (ModrinthApi.BaseUrl is a static hook).
// Environment.CurrentDirectory is process-global too: every host sets it (core paths are
// CWD-relative) and restores it on dispose.
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Web;
using FlintLauncher.Web.Services;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace FlintLauncher.Tests;

// --------------------------------------------------------------------------- host fixture

internal sealed class FlintApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _previousCwd;

    private FlintApiHost(WebApplication app, HttpClient client, string dataDir, string previousCwd, string token)
    {
        _app = app;
        Client = client;
        DataDir = dataDir;
        Token = token;
        _previousCwd = previousCwd;
    }

    public HttpClient Client { get; }

    public string DataDir { get; }

    public static async Task<FlintApiHost> StartAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"flint-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previousCwd = Environment.CurrentDirectory;
        var app = WebHostBuilder.BuildWebApplication(
        [
            "--data-dir", dataDir,
            "--urls", "http://127.0.0.1:0",
        ]);
        await app.StartAsync();
        var token = File.ReadAllText(Path.Combine(dataDir, ApiAuthService.TokenFileName)).Trim();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        // every API route (except /healthz and /api/v1/info) requires the data-dir token
        client.DefaultRequestHeaders.Add("X-Api-Token", token);
        return new FlintApiHost(app, client, dataDir, previousCwd, token);
    }

    /// <summary>A client WITHOUT credentials, for auth-rejection assertions.</summary>
    public HttpClient CreateUnauthorizedClient() =>
        new() { BaseAddress = new Uri(Client.BaseAddress!.ToString()) };

    public string Token { get; } = "";

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
                Directory.Delete(DataDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}

/// <summary>Minimal stub JSON server (device-code endpoints, news feed, modrinth search).</summary>
internal sealed class ApiStubServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private bool _disposed;

    public ApiStubServer(Func<HttpListenerContext, Task> handler)
    {
        Handler = handler;
        var port = 40000 + Random.Shared.Next(20000);
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Url = $"http://127.0.0.1:{port}";
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
        catch (ObjectDisposedException)
        {
        }
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
        var id = created.GetProperty("id").GetString()!;
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

        // rename: the core renames the directory -> the instance id follows ------------
        var rename = await host.PatchAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}",
            new { name = "Renamed Trip" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
        entry = list.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Renamed Trip");
        Assert.Equal("Renamed Trip", entry.GetProperty("id").GetString());
        id = entry.GetProperty("id").GetString()!;

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
            .RootElement.GetProperty("id").GetString()!;

        // PUT with kind-carrying values -------------------------------------------------
        Assert.Equal(HttpStatusCode.NoContent, (
            await host.PutAsync(
                $"/api/v1/instances/{Uri.EscapeDataString(id)}/settings",
                new
                {
                    overrides = new Dictionary<string, object>
                    {
                        ["OverrideJavaLocation"] = true,
                        ["JoinServerOnLaunch"] = true,
                        ["notes"] = "typed",
                        ["iconKey"] = "grass",
                    },
                })).StatusCode);

        var settings = JsonDocument.Parse(await (await host.GetAsync(
            $"/api/v1/instances/{Uri.EscapeDataString(id)}/settings")).Content.ReadAsStringAsync())
            .RootElement;
        // the settings map exposes the raw instance.cfg (INI) keys
        Assert.Equal(JsonValueKind.True, settings.GetProperty("OverrideJavaLocation").ValueKind);
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
        var profileId = account.GetProperty("profileId").GetString()!;
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
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("pack/instance.cfg");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("name=Pack Inst\n");
        }

        var post = await host.PostAsync("/api/v1/instances/import",
            new { url = new Uri(zipPath).ToString(), name = "Imported Pack" });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var taskId = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;

        var task = await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(20));
        Assert.Equal("import", task.GetProperty("kind").GetString());

        // the committed instance is discoverable and carries the pack name
        var list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
        var imported = list.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Imported Pack");
        var importedId = imported.GetProperty("id").GetString()!;

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
    public async Task News_Feed_Through_Stub_Rss_Server()
    {
        var rss = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title>Flint API news</title>
                <content>Hello from the stub feed</content>
                <id>https://flint.test/news/1</id>
              </entry>
            </feed>
            """;
        using var server = new ApiStubServer(async ctx =>
        {
            var bytes = Encoding.UTF8.GetBytes(rss);
            ctx.Response.ContentType = "application/xml";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });
        var previousFeed = Environment.GetEnvironmentVariable("FLINT_NEWS_RSS_URL");
        Environment.SetEnvironmentVariable("FLINT_NEWS_RSS_URL", server.Url + "/feed.xml");
        try
        {
            await using var host = await FlintApiHost.StartAsync();
            var response = await host.GetAsync("/api/v1/news");
            response.EnsureSuccessStatusCode();
            var entries = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(1, entries.GetArrayLength());
            Assert.Equal("Flint API news", entries[0].GetProperty("title").GetString());
            Assert.Equal("Hello from the stub feed", entries[0].GetProperty("content").GetString());
            Assert.Equal("https://flint.test/news/1", entries[0].GetProperty("link").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLINT_NEWS_RSS_URL", previousFeed);
        }
    }

    // ------------------------------------------------------------- full launch e2e

    private static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string? LauncherJarsRoot()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var launcherRoot = Path.Combine(repoRoot, "build");
        return File.Exists(Path.Combine(launcherRoot, "jars", "JavaCheck.jar")) &&
               File.Exists(Path.Combine(launcherRoot, "jars", "NewLaunch.jar"))
            ? launcherRoot
            : null;
    }

    [Fact]
    public async Task Launch_Through_Api_EndToEnd_With_FakeJava()
    {
        if (!IsUnix || LauncherJarsRoot() is not { } launcherRoot)
        {
            return; // needs the unix fake-java + the built NewLaunch.jar/JavaCheck.jar
        }

        var dataDir = Path.Combine(Path.GetTempPath(), $"flint-api-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previousCwd = Environment.CurrentDirectory;
        var previousLauncherRoot = JavaUtils.LauncherRootPath;
        JavaUtils.LauncherRootPath = launcherRoot;

        // seed an instance on disk the same way the LaunchFixture does (patched net.minecraft
        // profile loads fully offline) — the API discovers it at startup
        var instanceRoot = Path.Combine(dataDir, "instances", "e2e-inst");
        Directory.CreateDirectory(instanceRoot);
        await File.WriteAllTextAsync(Path.Combine(instanceRoot, "instance.cfg"), "name=E2E Instance\n");
        Directory.CreateDirectory(Path.Combine(instanceRoot, "patches"));
        await File.WriteAllTextAsync(Path.Combine(instanceRoot, "patches", "net.minecraft.json"),
            LaunchFixture.DefaultPatchJson);
        await File.WriteAllTextAsync(Path.Combine(instanceRoot, "mmc-pack.json"),
            """{"formatVersion":1,"components":[{"important":true,"uid":"net.minecraft"}]}""");

        var root = Path.Combine(dataDir, "scratch");
        Directory.CreateDirectory(root);
        var javaPath = WriteFakeJava(root);
        var javaUnixTime = new DateTimeOffset(File.GetLastWriteTimeUtc(javaPath)).ToUnixTimeMilliseconds();

        try
        {
            var app = WebHostBuilder.BuildWebApplication(
            [
                "--data-dir", dataDir,
                "--urls", "http://127.0.0.1:0",
            ]);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Add("X-Api-Token",
                File.ReadAllText(Path.Combine(dataDir, ApiAuthService.TokenFileName)).Trim());
            Environment.CurrentDirectory = dataDir;

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
                    ["JavaSignature"] = CheckJava.ComputeJavaSignature(javaUnixTime, javaPath),
                    ["JavaVersion"] = "17.0.2",
                    ["JavaArchitecture"] = "64",
                    ["JavaRealArchitecture"] = "arm64",
                    ["JavaVendor"] = "api-e2e",
                },
            });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            // launch offline: 202 + task id --------------------------------------------
            var launch = await client.PostAsJsonAsync("/api/v1/instances/e2e-inst/launch",
                new { mode = "offline", offlineName = "ApiTester" });
            Assert.Equal(HttpStatusCode.Accepted, launch.StatusCode);
            var taskId = JsonDocument.Parse(await launch.Content.ReadAsStringAsync())
                .RootElement.GetProperty("taskId").GetString()!;

            // the same instance cannot launch twice (core CanLaunch) --------------------
            var second = await client.PostAsJsonAsync("/api/v1/instances/e2e-inst/launch",
                new { mode = "offline", offlineName = "ApiTester" });
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("instance_unavailable", JsonDocument.Parse(
                await second.Content.ReadAsStringAsync())
                .RootElement.GetProperty("error").GetProperty("code").GetString());

            // AutoProceed is false for API launches: the task parks in "waiting" --------
            var waiting = await PollTaskState(client, taskId, "waiting", TimeSpan.FromSeconds(30));
            Assert.Equal("launch", waiting.GetProperty("kind").GetString());

            // premature abort endpoint exists; proceed opens the gate -------------------
            var proceed = await client.PostAsJsonAsync($"/api/v1/tasks/{taskId}/proceed", new { });
            proceed.EnsureSuccessStatusCode();

            var done = await PollTaskState(client, taskId, "succeeded", TimeSpan.FromSeconds(30));

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
                Assert.Contains("text/event-stream", stream.Content.Headers.ContentType!.ToString());
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
            JavaUtils.LauncherRootPath = previousLauncherRoot;
            Environment.CurrentDirectory = previousCwd;
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
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

[Collection("AuthEndpoints")]
public class FlintApiDeviceCodeTests : IDisposable
{
    private readonly ApiStubServer _server;
    private readonly string _msaDeviceCode;
    private string _msaToken;

    public FlintApiDeviceCodeTests()
    {
        // token endpoint answer is switchable per phase of the test
        _msaDeviceCode = """
            {"device_code":"dev123","user_code":"FLNT-CODE","verification_uri":"https://microsoft.test/link","expires_in":900,"interval":1}
            """;
        _msaToken = """{"error":"authorization_pending"}""";
        _server = new ApiStubServer(async ctx =>
        {
            var body = ctx.Request.Url!.AbsolutePath.EndsWith("/devicecode", StringComparison.Ordinal)
                ? _msaDeviceCode
                : _msaToken;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        AuthEndpoints.MsaDeviceCode = _server.Url + "/devicecode";
        AuthEndpoints.MsaToken = _server.Url + "/token";
    }

    public void Dispose()
    {
        AuthEndpoints.ResetToDefaults();
        _server.Dispose();
    }

    [Fact]
    public async Task DeviceCode_Start_Poll_Pending_Expired()
    {
        await using var host = await FlintApiHost.StartAsync();

        // start: 202 with the code triplet and an internal correlation id
        var start = await host.PostAsync("/api/v1/accounts/msa/devicecode", new { });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var started = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("FLNT-CODE", started.GetProperty("userCode").GetString());
        Assert.Equal("https://microsoft.test/link", started.GetProperty("verificationUri").GetString());
        Assert.Equal(900, started.GetProperty("expiresInSeconds").GetInt32());
        var deviceId = started.GetProperty("deviceId").GetString()!;
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
            state = JsonDocument.Parse(await next.Content.ReadAsStringAsync())
                .RootElement.GetProperty("state").GetString()!;
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
        LastSearchQuery = ctx.Request.Url!.Query;
        var bytes = Encoding.UTF8.GetBytes(HitsJson);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public FlintApiModrinthSearchTests()
    {
        ModrinthApi.BaseUrl = _server.Url;
    }

    public void Dispose()
    {
        ModrinthApi.BaseUrl = "https://api.modrinth.com/v2";
        _server.Dispose();
    }

    [Fact]
    public async Task Modrinth_Search_Passes_Through_Core_Api()
    {
        await using var host = await FlintApiHost.StartAsync();

        var response = await host.GetAsync(
            "/api/v1/modplatform/modrinth/search?query=jei&version=1.20.1&loader=fabric&index=downloads&offset=40");
        response.EnsureSuccessStatusCode();
        Assert.Contains("offset=40", LastSearchQuery);
        var hits = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, hits.GetArrayLength());
        Assert.Equal("proj1", hits[0].GetProperty("id").GetString());
        Assert.Equal("jei", hits[0].GetProperty("slug").GetString());
        Assert.Equal("Just Enough Items", hits[0].GetProperty("title").GetString());
        Assert.Equal("https://cdn.test/jei.png", hits[0].GetProperty("iconUrl").GetString());
        Assert.Equal("mezz", hits[0].GetProperty("author").GetString());
        Assert.Equal(12345L, hits[0].GetProperty("downloads").GetInt64());
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
                "--data-dir", dataDir,
                "--urls", "http://127.0.0.1:0",
                "--no-auth",
            ]);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
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
