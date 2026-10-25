// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the seeded-profile launch pipeline data pieces: createLaunchScript's exact line
// set (MinecraftInstance::createLaunchScript), processMinecraftArgs token substitution
// (including the auth tokens) and LaunchTask's censorPrivateInfo session filter.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using Xunit;

namespace FlintLauncher.Tests;

public class LaunchScriptTests
{
    [Fact]
    public async Task CreateLaunchScript_ProducesTheExactLineSet()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var session = fixture.OfflineSession();

        var script = instance.CreateLaunchScript(session, null);
        var lines = script.Split('\n');

        var gameDir = instance.GameRoot;
        var assetsRoot = instance.AssetsRoot; // the five-root reform: <store>/assets
        var expected = new[]
        {
            "mainClass net.minecraft.client.main.Main",
            // generic minecraft params (processMinecraftArgs with no target: no --server/--port)
            "param --username",
            "param Steve",
            "param --version",
            "param 1.20.1",
            "param --gameDir",
            $"param {gameDir}",
            "param --assetsDir",
            $"param {assetsRoot}",
            "param --assetIndex",
            "param 5",
            "param --uuid",
            $"param {session.Uuid}",
            "param --accessToken",
            "param 0",
            "param --userType",
            "param offline",
            "param --userProperties",
            "param {}",
            // window size, title and state
            "windowTitle Flint Launcher: Unnamed Instance",
            "windowParams 854x480",
            // launcher info
            "launcherBrand Flint Launcher",
            "launcherVersion 0.1.0",
            // instance info
            "instanceName Unnamed Instance",
            "instanceIconKey Unnamed Instance",
            "instanceIconPath icon.png",
            // legacy auth (an offline account's token is "0", so the session id is the
            // token:0:<profileId> form — MinecraftAccount::fillSession)
            "userName Steve",
            $"sessionId token:0:{session.Uuid}",
            // traits (from the seeded patch)
            "traits FirstThreadOnMacOS",
            // launcher (no legacy trait -> standard)
            "launcher standard",
            "", // trailing newline
        };
        Assert.Equal(expected, lines);
    }

    [Fact]
    public async Task CreateLaunchScript_ServerTarget_AddsServerLines()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var session = fixture.OfflineSession();
        var target = new MinecraftTarget("mc.example.com", 25565, "");

        var script = instance.CreateLaunchScript(session, target);

        Assert.Contains("serverAddress mc.example.com\n", script);
        Assert.Contains("serverPort 25565\n", script);
        // When using a launch script, the server parameters are handled by it: the params
        // must not contain --server/--port.
        Assert.DoesNotContain("param --server\n", script);
        // but the auth params are still there
        Assert.Contains("param --username\nparam Steve\n", script);
    }

    [Fact]
    public async Task CreateLaunchScript_WorldTarget_AddsWorldLine()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var target = new MinecraftTarget("", 25565, "My World");

        var script = instance.CreateLaunchScript(null, target);

        Assert.Contains("worldName My World\n", script);
        Assert.DoesNotContain("serverAddress", script);
    }

    [Fact]
    public async Task CreateLaunchScript_LegacyTraits_UseLegacyLauncher()
    {
        using var fixture = new LaunchFixture();
        var patch = LaunchFixture.DefaultPatchJson.Replace("\"+traits\": [\"FirstThreadOnMacOS\"]",
            "\"+traits\": [\"FirstThreadOnMacOS\", \"legacyLaunch\"]");
        var instance = await fixture.CreateInstance(patch);
        Assert.True(instance.IsLegacy());

        var script = instance.CreateLaunchScript(null, null);

        Assert.Contains("launcher legacy\n", script);
    }

    [Fact]
    public async Task CreateLaunchScript_BrokenProfile_HasNoMainClass()
    {
        using var fixture = new LaunchFixture();
        // a patch that cannot be parsed yields a problem-carrying VersionFile; the profile
        // builds (with the problem severity applied) but has no main class
        var instance = await fixture.CreateInstance("{ not json ");

        Assert.NotNull(instance.GetPackProfile().GetProfile());
        var script = instance.CreateLaunchScript(null, null);
        Assert.DoesNotContain("mainClass", script);
        Assert.Contains("launcher standard\n", script);
    }

    [Fact]
    public async Task ProcessMinecraftArgs_SubstitutesProfileAndAuthTokens()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var session = fixture.OfflineSession();

        var args = instance.ProcessMinecraftArgs(session, null);

        Assert.Equal(
        [
            "--username", "Steve",
            "--version", "1.20.1",
            "--gameDir", instance.GameRoot.Value,
            "--assetsDir", instance.AssetsRoot.Value,
            "--assetIndex", "5",
            "--uuid", session.Uuid,
            "--accessToken", "0",
            "--userType", "offline",
            "--userProperties", "{}",
        ], args);
    }

    [Fact]
    public async Task ProcessMinecraftArgs_DemoSession_AppendsDemoFlag()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var session = new AuthSession();
        session.MakeDemo("Steve", "abc");

        var args = instance.ProcessMinecraftArgs(session, null);

        Assert.Equal("--demo", args[^1]);
    }

    [Fact]
    public async Task ProcessMinecraftArgs_ServerTarget_AddsServerArgs()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var target = new MinecraftTarget("mc.example.com", 25565, "");

        var args = instance.ProcessMinecraftArgs(null, target);

        // the C++ appends the target arguments after the split profile arguments
        Assert.Equal("--username", args[0]);
        Assert.Equal("--server", args[^4]);
        Assert.Equal("mc.example.com", args[^3]);
        Assert.Equal("--port", args[^2]);
        Assert.Equal("25565", args[^1]);
    }

    [Fact]
    public async Task CensorPrivateInfo_ReplacesSessionTokens()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        var session = fixture.OfflineSession();

        // MinecraftInstance::createCensorFilterFromSession shape
        var filter = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [session.Session] = "<SESSION ID>", // "-" is filtered upstream; offline session is "-"
            ["0"] = "<ACCESS TOKEN>",           // "0" would be filtered upstream
            [session.Uuid] = "<PROFILE ID>",
            ["super-secret-token"] = "<ACCESS TOKEN>"
        };
        task.SetCensorFilter(filter);

        var line = task.CensorPrivateInfo("logging in with super-secret-token and profile " + session.Uuid);
        Assert.Equal("logging in with <ACCESS TOKEN> and profile <PROFILE ID>", line);
    }

    [Fact]
    public async Task SubstituteVariables_ExpandsInstanceVariables()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        instance.SetName("My Instance");
        var task = LaunchTask.Create(instance, fixture.CreateServices());

        var args = task.SubstituteVariables("-cp ${INST_DIR} $INST_NAME %INST_NAME%");

        Assert.Equal(4, args.Count);
        Assert.Equal("-cp", args[0]);
        Assert.Equal(instance.InstanceRoot.Value, args[1]);
        Assert.Equal("My Instance", args[2]);
        Assert.Equal("%INST_NAME%", args[3]); // only $VAR/${VAR} are expanded
    }
}
