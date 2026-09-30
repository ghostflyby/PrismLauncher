// SPDX-License-Identifier: GPL-3.0-only
// fix-3/fix-4 API-surface tests: the completed settings whitelist (new groups + aligned
// defaults), the proxy/retry/timeout wiring reaching the shared Http stack, the world
// copy/detail endpoints, the paste/about/meta-cache endpoints and the launch join-target
// parameters (e2e with the fake java).
//
// All classes join "LauncherJars": the hosts chdir (process-global) and the Http proxy/
// retry hooks are process-global statics that must not race the other API suites.
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Net;
using FlintLauncher.Nbt;
using FlintLauncher.Web;
using FlintLauncher.Web.Services;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace FlintLauncher.Tests;

// --------------------------------------------------------------------- settings + misc (fix-3/4)

[Collection("LauncherJars")]
public class CoverageFixSettingsApiTests
{
    [Fact]
    public async Task Settings_Get_Exposes_Cpp_Aligned_Defaults()
    {
        await using var host = await FlintApiHost.StartAsync();
        var groups = await GetGroups(host);

        var launcher = groups.GetProperty("launcher");
        Assert.Equal(1, launcher.GetProperty("NumberOfManualRetries").GetInt32());
        Assert.Equal(60, launcher.GetProperty("RequestTimeout").GetInt32());
        Assert.Equal(10, launcher.GetProperty("NumberOfConcurrentTasks").GetInt32());
        Assert.Equal(6, launcher.GetProperty("NumberOfConcurrentDownloads").GetInt32());
        Assert.True(launcher.GetProperty("ShowConsoleOnError").GetBoolean());
        Assert.True(launcher.GetProperty("ConsoleOverflowStop").GetBoolean());
        Assert.Equal(100000, launcher.GetProperty("ConsoleMaxLines").GetInt32());
        Assert.False(launcher.GetProperty("IgnoreJavaWizard").GetBoolean());
        Assert.True(launcher.GetProperty("DownloadGameFilesDuringInstanceCreation").GetBoolean());

        var minecraft = groups.GetProperty("minecraft");
        Assert.Equal(128, minecraft.GetProperty("PermGen").GetInt32());
        // RAM-derived (SysInfo::defaultMaxJvmMem port); the host registered the machine's value
        Assert.Equal(GlobalSettingsRegistration.DefaultMaxMemAlloc,
            minecraft.GetProperty("MaxMemAlloc").GetInt32());
        Assert.True(minecraft.GetProperty("LowMemWarning").GetBoolean());
        Assert.True(minecraft.GetProperty("ShowGlobalGameTime").GetBoolean());
        Assert.False(minecraft.GetProperty("UseZink").GetBoolean());
        Assert.False(minecraft.GetProperty("OnlineFixes").GetBoolean());

        Assert.Equal("", groups.GetProperty("java").GetProperty("JavaPath").GetString());
        Assert.Equal("127.0.0.1", groups.GetProperty("proxy").GetProperty("ProxyAddr").GetString());

        var dirs = groups.GetProperty("dirs");
        Assert.Equal("mods", dirs.GetProperty("CentralModsDir").GetString());
        Assert.Equal("icons", dirs.GetProperty("IconsDir").GetString());
        Assert.Equal("skins", dirs.GetProperty("SkinsDir").GetString());
        Assert.Equal("java", dirs.GetProperty("JavaDir").GetString());
        Assert.False(string.IsNullOrEmpty(dirs.GetProperty("DownloadsDir").GetString()));

        Assert.Equal("", groups.GetProperty("language").GetProperty("Language").GetString());
        Assert.Equal("", groups.GetProperty("meta").GetProperty("MetaURLOverride").GetString());
        Assert.Equal("", groups.GetProperty("meta").GetProperty("ResourceURLOverride").GetString());
        Assert.Equal("", groups.GetProperty("meta").GetProperty("LegacyFMLLibsURLOverride").GetString());
    }

    [Fact]
    public async Task Settings_New_Groups_Are_Writable_And_Unknown_Keys_Rejected()
    {
        await using var host = await FlintApiHost.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
        {
            group = "dirs",
            values = new Dictionary<string, object>
            {
                ["CentralModsDir"] = "custom-mods",
                ["DownloadsDirWatchRecursive"] = true,
            },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
        {
            group = "language",
            values = new Dictionary<string, object> { ["Language"] = "qq" },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
        {
            group = "meta",
            values = new Dictionary<string, object>
            {
                ["ResourceURLOverride"] = "https://mirror.test/resources/",
                ["LegacyFMLLibsURLOverride"] = "https://mirror.test/fmllibs/",
            },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
        {
            group = "launcher",
            values = new Dictionary<string, object>
            {
                ["IgnoreJavaWizard"] = true,
                ["ConsoleMaxLines"] = 5000,
            },
        })).StatusCode);

        var groups = await GetGroups(host);
        Assert.Equal("custom-mods", groups.GetProperty("dirs").GetProperty("CentralModsDir").GetString());
        Assert.True(groups.GetProperty("dirs").GetProperty("DownloadsDirWatchRecursive").GetBoolean());
        Assert.Equal("qq", groups.GetProperty("language").GetProperty("Language").GetString());
        Assert.Equal("https://mirror.test/resources/",
            groups.GetProperty("meta").GetProperty("ResourceURLOverride").GetString());
        Assert.Equal("https://mirror.test/fmllibs/",
            groups.GetProperty("meta").GetProperty("LegacyFMLLibsURLOverride").GetString());
        Assert.True(groups.GetProperty("launcher").GetProperty("IgnoreJavaWizard").GetBoolean());
        Assert.Equal(5000, groups.GetProperty("launcher").GetProperty("ConsoleMaxLines").GetInt32());

        // whitelist still enforced for the new groups
        var unknown = await host.PutAsync("/api/v1/settings", new
        {
            group = "meta",
            values = new Dictionary<string, object> { ["NotAMirrorKey"] = 1 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("unknown_setting", JsonDocument.Parse(await unknown.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        var badGroup = await host.PutAsync("/api/v1/settings", new
        {
            group = "not-a-group",
            values = new Dictionary<string, object> { ["x"] = 1 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badGroup.StatusCode);
    }

    [Fact]
    public async Task Proxy_Retry_Timeout_Settings_Reach_The_Http_Stack()
    {
        var previousUa = Http.UserAgent;
        var previousRetries = Http.ManualRetries;
        var previousTimeout = Http.RequestTimeout;
        try
        {
            await using var host = await FlintApiHost.StartAsync();

            Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
            {
                group = "proxy",
                values = new Dictionary<string, object>
                {
                    ["ProxyType"] = "HTTP",
                    ["ProxyAddr"] = "127.0.0.1",
                    ["ProxyPort"] = 39999,
                },
            })).StatusCode);
            Assert.Equal(("HTTP", "127.0.0.1", 39999, "", ""), Http.ProxyConfig);

            Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
            {
                group = "launcher",
                values = new Dictionary<string, object>
                {
                    ["NumberOfManualRetries"] = 3,
                    ["RequestTimeout"] = 90,
                },
            })).StatusCode);
            Assert.Equal(3, Http.ManualRetries);
            Assert.Equal(TimeSpan.FromSeconds(90), Http.RequestTimeout);

            Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
            {
                group = "api",
                values = new Dictionary<string, object> { ["UserAgentOverride"] = "Flint-Api-Agent/1.0" },
            })).StatusCode);
            Assert.Equal("Flint-Api-Agent/1.0", Http.UserAgent);

            // "None" disables the proxy again without a restart
            Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsync("/api/v1/settings", new
            {
                group = "proxy",
                values = new Dictionary<string, object> { ["ProxyType"] = "None" },
            })).StatusCode);
            Assert.Equal(("None", "127.0.0.1", 39999, "", ""), Http.ProxyConfig);
        }
        finally
        {
            // restore the steady state a host startup produces (the registered defaults)
            Http.Configure(FlintConfig.Default.UserAgent, "None", "127.0.0.1", 8080, "", "");
            Http.ManualRetries = previousRetries;
            Http.RequestTimeout = previousTimeout;
            Assert.Equal(("None", "127.0.0.1", 8080, "", ""), Http.ProxyConfig);
            Assert.Equal(FlintConfig.Default.UserAgent, Http.UserAgent);
        }
    }

    [Fact]
    public async Task About_Endpoint_Exposes_The_Static_Identity()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/about");
        response.EnsureSuccessStatusCode();
        var about = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Flint Launcher", about.GetProperty("name").GetString());
        Assert.Equal(FlintConfig.Default.PrintableVersionString(), about.GetProperty("version").GetString());
        Assert.False(string.IsNullOrEmpty(about.GetProperty("copyright").GetString()));
        Assert.Equal(FlintConfig.Default.MatrixUrl, about.GetProperty("matrixUrl").GetString());
        Assert.Equal(FlintConfig.Default.DiscordUrl, about.GetProperty("discordUrl").GetString());
        Assert.Equal(FlintConfig.Default.SubredditUrl, about.GetProperty("subredditUrl").GetString());
        Assert.Equal(FlintConfig.Default.BugTrackerUrl, about.GetProperty("bugTrackerUrl").GetString());
    }

    [Fact]
    public async Task Meta_Cache_Evict_Returns_NoContent()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.DeleteAsync("/api/v1/meta-cache");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // the settings groups serialize the raw C++ setting ids as dictionary keys — the JSON
    // naming policy only applies to record properties (groups.launcher.ShowConsoleOnError)
    private static async Task<JsonElement> GetGroups(FlintApiHost host)
    {
        var response = await host.GetAsync("/api/v1/settings");
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return json.GetProperty("groups").Clone();
    }
}

// ------------------------------------------------------------------------------ worlds (fix-4)

[Collection("LauncherJars")]
public class CoverageFixWorldsApiTests : IAsyncDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"flint-fix4-{Guid.NewGuid():N}");
    private WaveApiHost? _host;

    private static void SeededWorld(string worldDir, string name, long seed)
    {
        Directory.CreateDirectory(worldDir);
        var data = new NbtCompound();
        data.Put("LevelName", new NbtString(name));
        data.Put("LastPlayed", new NbtLong(1700000000000));
        data.Put("GameType", new NbtInt(1));
        var worldGen = new NbtCompound();
        worldGen.Put("seed", new NbtLong(seed));
        data.Put("WorldGenSettings", worldGen);
        var root = new NbtCompound();
        root.Put("Data", data);
        File.WriteAllBytes(Path.Combine(worldDir, "level.dat"), NbtIo.WriteCompoundGzip(root));
    }

    private string SavesDir => Path.Combine(_dataDir, "instances", "w-four", "minecraft", "saves");

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
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
    public async Task World_Copy_Endpoint_Copies_The_Folder_Within_Saves()
    {
        WaveSeed.Instance(_dataDir, "w-four", "Worlds Four");
        SeededWorld(Path.Combine(SavesDir, "world_one"), "World One", 42);
        _host = await WaveApiHost.StartAsync(_dataDir);

        var copy = await _host.PostAsync("/api/v1/instances/w-four/worlds/world_one/copy",
            new { name = "World One Copy" });
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var view = JsonDocument.Parse(await copy.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("World One Copy", view.GetProperty("name").GetString());
        Assert.Equal("World One Copy", view.GetProperty("folderName").GetString());

        // the copy is a real folder inside saves with its own level.dat
        var copiedDir = Path.Combine(SavesDir, "World One Copy");
        Assert.True(Directory.Exists(copiedDir));
        Assert.True(File.Exists(Path.Combine(copiedDir, "level.dat")));

        // the listing shows both worlds
        var worlds = JsonDocument.Parse(await _host.Client.GetStringAsync(
            "/api/v1/instances/w-four/worlds")).RootElement;
        Assert.Equal(2, worlds.GetArrayLength());

        // errors: missing body name, unknown world
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.PostAsync(
            "/api/v1/instances/w-four/worlds/world_one/copy", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.PostAsync(
            "/api/v1/instances/w-four/worlds/ghost/copy", new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task World_Detail_Endpoint_Returns_Seed_And_Icon_Path()
    {
        WaveSeed.Instance(_dataDir, "w-four", "Worlds Four");
        SeededWorld(Path.Combine(SavesDir, "world_one"), "World One", 123456789);
        File.WriteAllBytes(Path.Combine(SavesDir, "world_one", "icon.png"), [1, 2, 3]);
        _host = await WaveApiHost.StartAsync(_dataDir);

        var response = await _host.Client.GetAsync("/api/v1/instances/w-four/worlds/world_one");
        response.EnsureSuccessStatusCode();
        var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("World One", detail.GetProperty("name").GetString());
        Assert.Equal("world_one", detail.GetProperty("folderName").GetString());
        Assert.Equal(123456789L, detail.GetProperty("seed").GetInt64());
        Assert.Equal(1700000000000L, detail.GetProperty("lastPlayed").GetInt64());
        Assert.Equal("Creative", detail.GetProperty("gameType").GetString());
        Assert.True(detail.GetProperty("sizeBytes").GetInt64() > 0);
        var iconPath = detail.GetProperty("iconPath").GetString();
        Assert.False(string.IsNullOrEmpty(iconPath));
        Assert.True(File.Exists(iconPath));

        // a world without an icon reports an empty path
        SeededWorld(Path.Combine(SavesDir, "no_icon"), "No Icon", 7);
        detail = JsonDocument.Parse(await _host.Client.GetStringAsync(
            "/api/v1/instances/w-four/worlds/no_icon")).RootElement;
        Assert.Equal("", detail.GetProperty("iconPath").GetString());
        Assert.Equal(7, detail.GetProperty("seed").GetInt64());

        Assert.Equal(HttpStatusCode.NotFound,
            (await _host.Client.GetAsync("/api/v1/instances/w-four/worlds/ghost")).StatusCode);
    }
}

// ------------------------------------------------------- launch join targets + paste (fix-4)

[Collection("LauncherJars")]
public class CoverageFixLaunchJoinApiTests
{
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

    [Fact]
    public async Task Launch_Join_Target_Bodies_Are_Validated()
    {
        // validation runs before the instance lookup, so a ghost instance id is fine here
        await using var host = await FlintApiHost.StartAsync();

        // 'server' and 'world' are mutually exclusive join targets
        var both = await host.PostAsync("/api/v1/instances/ghost/launch", new
        {
            mode = "offline",
            offlineName = "X",
            server = new { address = "server.example", port = 25565 },
            world = "Some World",
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal("invalid_request", JsonDocument.Parse(await both.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        // out-of-range port
        var badPort = await host.PostAsync("/api/v1/instances/ghost/launch", new
        {
            mode = "offline",
            server = new { address = "server.example", port = 70000 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badPort.StatusCode);

        // missing address
        var noAddress = await host.PostAsync("/api/v1/instances/ghost/launch", new
        {
            mode = "offline",
            server = new { address = "" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noAddress.StatusCode);

        // a world-only body passes validation and reaches the (missing) instance -> 404
        var worldOnly = await host.PostAsync("/api/v1/instances/ghost/launch", new
        {
            mode = "offline",
            world = "Some World",
        });
        Assert.Equal(HttpStatusCode.NotFound, worldOnly.StatusCode);
    }

    [Fact]
    public async Task Launch_Join_Targets_And_Paste_EndToEnd_With_FakeJava()
    {
        if (!IsUnix || LauncherJarsRoot() is not { } launcherRoot)
        {
            return; // needs the unix fake-java + the built NewLaunch.jar/JavaCheck.jar
        }

        // the paste stub: records the last decoded content, answers mclo.gs-style
        string? lastPasteContent = null;
        using var pasteStub = new ApiStubServer(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            if (raw.StartsWith("content=", StringComparison.Ordinal))
            {
                lastPasteContent = Uri.UnescapeDataString(raw["content=".Length..]);
            }
            var bytes = Encoding.UTF8.GetBytes("""{"success":true,"url":"https://mclo.gs/FLINT1"}""");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var dataDir = Path.Combine(Path.GetTempPath(), $"flint-api-join-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previousCwd = Environment.CurrentDirectory;

        var instanceRoot = Path.Combine(dataDir, "instances", "join-inst");
        Directory.CreateDirectory(instanceRoot);
        await File.WriteAllTextAsync(Path.Combine(instanceRoot, "instance.cfg"), "name=Join Instance\n");
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
            ],
            // the launch pipeline's jar lookups resolve against this install root (the former
            // JavaUtils.LauncherRootPath static test hook, now LaunchServices.Paths.InstallRoot)
            paths: FlintPaths.FromDataDir(dataDir) with { InstallRoot = launcherRoot });
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Add("X-Api-Token",
                File.ReadAllText(Path.Combine(dataDir, ApiAuthService.TokenFileName)).Trim());
            Environment.CurrentDirectory = dataDir;

            var put = await client.PutAsJsonAsync("/api/v1/instances/join-inst/settings", new
            {
                overrides = new Dictionary<string, object>
                {
                    ["OverrideJavaLocation"] = true,
                    ["JavaPath"] = javaPath,
                    ["JavaSignature"] = CheckJava.ComputeJavaSignature(javaUnixTime, javaPath),
                    ["JavaVersion"] = "17.0.2",
                    ["JavaArchitecture"] = "64",
                    ["JavaRealArchitecture"] = "arm64",
                    ["JavaVendor"] = "join-e2e",
                },
            });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            // point the paste provider at the stub (PastebinCustomAPIBase + type Mclogs keeps
            // the "/1/log" endpoint path; nothing may reach the real paste services)
            var pastePut = await client.PutAsJsonAsync("/api/v1/settings", new
            {
                group = "api",
                values = new Dictionary<string, object>
                {
                    ["PastebinCustomAPIBase"] = pasteStub.Url,
                },
            });
            Assert.Equal(HttpStatusCode.NoContent, pastePut.StatusCode);

            // launch #1: direct server join target (bypasses the instance setting) ------------
            var launch = await client.PostAsJsonAsync("/api/v1/instances/join-inst/launch", new
            {
                mode = "offline",
                offlineName = "ApiTester",
                server = new { address = "127.0.0.1", port = 25599 },
            });
            Assert.Equal(HttpStatusCode.Accepted, launch.StatusCode);
            var taskId = JsonDocument.Parse(await launch.Content.ReadAsStringAsync())
                .RootElement.GetProperty("taskId").GetString()!;
            await PollTaskState(client, taskId, "waiting", TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
                $"/api/v1/tasks/{taskId}/proceed", new { })).StatusCode);
            await PollTaskState(client, taskId, "succeeded", TimeSpan.FromSeconds(30));

            var log = JsonDocument.Parse(
                await client.GetStringAsync($"/api/v1/tasks/{taskId}/log?since=0")).RootElement;
            var lines = log.GetProperty("lines").EnumerateArray().ToList();
            Assert.Contains(lines, l => l.GetProperty("line").GetString() == "serverAddress 127.0.0.1");
            Assert.Contains(lines, l => l.GetProperty("line").GetString() == "serverPort 25599");

            // paste a finished task's buffered log ------------------------------------------
            var pasteTask = await client.PostAsJsonAsync("/api/v1/paste", new { taskId });
            Assert.Equal(HttpStatusCode.Created, pasteTask.StatusCode);
            Assert.Equal("https://mclo.gs/FLINT1", JsonDocument.Parse(
                await pasteTask.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString());
            Assert.NotNull(lastPasteContent);
            Assert.Contains("serverAddress 127.0.0.1", lastPasteContent);

            // launch #2: direct world join target on the finished instance -------------------
            var launch2 = await client.PostAsJsonAsync("/api/v1/instances/join-inst/launch", new
            {
                mode = "offline",
                offlineName = "ApiTester",
                world = "Speedrun World",
            });
            Assert.Equal(HttpStatusCode.Accepted, launch2.StatusCode);
            var taskId2 = JsonDocument.Parse(await launch2.Content.ReadAsStringAsync())
                .RootElement.GetProperty("taskId").GetString()!;
            await PollTaskState(client, taskId2, "waiting", TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
                $"/api/v1/tasks/{taskId2}/proceed", new { })).StatusCode);
            await PollTaskState(client, taskId2, "succeeded", TimeSpan.FromSeconds(30));

            var log2 = JsonDocument.Parse(
                await client.GetStringAsync($"/api/v1/tasks/{taskId2}/log?since=0")).RootElement;
            var lines2 = log2.GetProperty("lines").EnumerateArray().ToList();
            Assert.Contains(lines2, l => l.GetProperty("line").GetString() == "worldName Speedrun World");

            // raw content paste (the core anonymization applies) ------------------------------
            var paste = await client.PostAsJsonAsync("/api/v1/paste", new
            {
                content = "home dir /Users/ghostflyby/secret/session.log\npaste body",
            });
            Assert.Equal(HttpStatusCode.Created, paste.StatusCode);
            Assert.Equal("https://mclo.gs/FLINT1", JsonDocument.Parse(
                await paste.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString());
            Assert.NotNull(lastPasteContent);
            Assert.Contains("/Users/********/", lastPasteContent);
            Assert.DoesNotContain("ghostflyby", lastPasteContent);
            Assert.Contains("paste body", lastPasteContent);

            // error shapes --------------------------------------------------------------------
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsJsonAsync("/api/v1/paste", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/paste", new
            {
                content = "x",
                taskId = "t1-abc",
            })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/paste", new
            {
                taskId = "nope",
            })).StatusCode);
        }
        finally
        {
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
}
