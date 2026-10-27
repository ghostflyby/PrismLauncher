// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the CurseForge update check: a local mod with Flame .pw.toml metadata
// (murmur2 hash + project/file ids), the stub returning a newer file -> update candidate
// mapped (with the changelog fetched), and the blocked-update path (no download URL)
// surfacing through checkFailed with the manual-download recover URL.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.Versioning;
using TruePath;
using Xunit;
using PackwizModel = FlintLauncher.Core.ModPlatform.Packwiz.Packwiz;

namespace FlintLauncher.Tests;

[Collection("FlameApi")]
public class FlameCheckUpdateTests : IDisposable
{
    private readonly FlameApi _flameApi = new(TestNet.Http);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-flame-update-{Guid.NewGuid():N}");
    private readonly StubFlameServer _server = new();

    public FlameCheckUpdateTests()
    {
        Directory.CreateDirectory(_root);
        _flameApi.BaseUrl = _server.Url;
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

    private string TempPath(string name) => Path.Combine(_root, name);

    private (Mod Mod, string OldSha1) SetupInstalledMod(byte[] jarBytes)
    {
        var modsDir = TempPath("mods");
        Directory.CreateDirectory(Path.Combine(modsDir, ".index"));
        var jarPath = Path.Combine(modsDir, "test.jar");
        File.WriteAllBytes(jarPath, jarBytes);

        // installed metadata: the sha1 the CurseForge API reported when the file was
        // installed (the Flame branch of the metadata flow stores the API file hash)
        var oldSha1 = Convert.ToHexString(SHA1.HashData(jarBytes))
            .ToLowerInvariant();
        var metadata = new PackwizModel.Mod
        {
            Slug = "test-mod",
            Name = "Test Mod",
            Filename = "test.jar",
            Provider = ResourceProvider.Flame,
            ProjectId = "123",
            FileId = "456",
            VersionNumber = "1.0.0",
            HashFormat = "sha1",
            Hash = oldSha1,
            Mode = "metadata:curseforge",
            Loaders = ModLoaderType.Forge,
        };
        PackwizModel.UpdateModIndex(new AbsolutePath(Path.Combine(modsDir, ".index")), metadata);

        var mod = new Mod(new AbsolutePath(jarPath));
        return (mod, oldSha1);
    }

    [Fact]
    public async Task CheckUpdate_NewerFileWithHash_DetectsUpdateAndChangelog()
    {
        var (mod, oldSha1) = SetupInstalledMod("old flame jar content"u8.ToArray());

        // the API answers with a newer file (different sha1, later date)
        _server.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.FileJson(_server.Url, fileId: 789,
                displayName: "Test Mod 2.0", fileDate: "2024-06-01T00:00:00Z", sha1: "deadbeef" + oldSha1[8..],
                gameVersions: """["1.18.2", "forge"]"""))),
        }.ToJsonString();

        var check = new FlameCheckUpdate([mod], [new McVersion("1.18.2")], [ModLoaderType.Forge],
            _ => MetadataHandler.Get(new AbsolutePath(Path.Combine(TempPath("mods"), ".index")), "test-mod"), flame: _flameApi);

        var failures = new List<(Resource Resource, string Reason, string RecoverUrl)>();
        check.CheckFailed += (resource, reason, recoverUrl) => failures.Add((resource, reason, recoverUrl));

        await check.StartAsync();

        Assert.True(check.WasSuccessful, check.FailReason);
        Assert.Empty(failures);
        var update = Assert.Single(check.GetUpdates());
        // the update record carries the resource's display name, like the C++ fake pack
        Assert.Equal("test", update.Name);
        Assert.Equal("1.0.0", update.OldVersion);
        Assert.Equal("Test Mod 2.0", update.NewVersion);
        Assert.Equal(IndexedVersionTypeValue.Release, update.NewVersionType.Value);
        Assert.Equal(ResourceProvider.Flame, update.Provider);
        Assert.Equal("789", update.Version.FileId);
        // the changelog was fetched from the API for the update record
        Assert.Equal("changelog text", update.Changelog);
    }

    [Fact]
    public async Task CheckUpdate_SameFileIdMeansNoUpdate()
    {
        var (mod, oldSha1) = SetupInstalledMod("same content"u8.ToArray());

        // the latest file is still file 456 (the installed one) and reports the same sha1
        // -> no update
        _server.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.FileJson(_server.Url, fileId: 456,
                displayName: "Test Mod 1.0", fileDate: "2024-06-01T00:00:00Z", sha1: oldSha1))),
        }.ToJsonString();

        var check = new FlameCheckUpdate([mod], [new McVersion("1.18.2")], [ModLoaderType.Forge],
            _ => MetadataHandler.Get(new AbsolutePath(Path.Combine(TempPath("mods"), ".index")), "test-mod"), flame: _flameApi);
        await check.StartAsync();

        Assert.True(check.WasSuccessful, check.FailReason);
        Assert.Empty(check.GetUpdates());
    }

    [Fact]
    public async Task CheckUpdate_BlockedUpdate_ReportsRecoverUrl()
    {
        var (mod, _) = SetupInstalledMod("old flame jar content"u8.ToArray());

        // newer file 789 has no download URL (CF blocks third-party launchers)
        _server.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.FileJson(_server.Url, fileId: 789,
                displayName: "Test Mod 2.0", fileDate: "2024-06-01T00:00:00Z",
                downloadUrlPath: null))),
        }.ToJsonString();
        // collectBlockedMods fetches the project info (single addon -> GET /mods/{id})
        _server.ProjectJson = new JsonObject
        {
            ["data"] = JsonNode.Parse(FlamePayloads.ProjectJson(id: 123)),
        }.ToJsonString();

        var check = new FlameCheckUpdate([mod], [new McVersion("1.18.2")], [ModLoaderType.Forge],
            _ => MetadataHandler.Get(new AbsolutePath(Path.Combine(TempPath("mods"), ".index")), "test-mod"), flame: _flameApi);

        var failures = new List<(Resource Resource, string Reason, string RecoverUrl)>();
        check.CheckFailed += (resource, reason, recoverUrl) => failures.Add((resource, reason, recoverUrl));

        await check.StartAsync();

        Assert.True(check.WasSuccessful, check.FailReason);
        Assert.Empty(check.GetUpdates());
        var failure = Assert.Single(failures);
        Assert.Equal("Resource has a new update available, but is not downloadable using CurseForge.",
            failure.Reason);
        Assert.Equal("https://www.curseforge.com/projects/test-mod/download/789", failure.RecoverUrl);
    }

    [Fact]
    public async Task CheckUpdate_LoaderMismatch_ReportsCheckFailed()
    {
        var (mod, _) = SetupInstalledMod("old flame jar content"u8.ToArray());

        // the newest file for the project targets fabric only -> nothing matches the
        // forge instance
        _server.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.FileJson(_server.Url, fileId: 789,
                displayName: "Test Mod 2.0", fileDate: "2024-06-01T00:00:00Z",
                gameVersions: """["1.18.2", "fabric"]"""))),
        }.ToJsonString();

        var check = new FlameCheckUpdate([mod], [new McVersion("1.18.2")], [ModLoaderType.Forge],
            _ => MetadataHandler.Get(new AbsolutePath(Path.Combine(TempPath("mods"), ".index")), "test-mod"), flame: _flameApi);

        var failures = new List<(Resource Resource, string Reason, string RecoverUrl)>();
        check.CheckFailed += (resource, reason, recoverUrl) => failures.Add((resource, reason, recoverUrl));

        await check.StartAsync();

        Assert.True(check.WasSuccessful, check.FailReason);
        Assert.Empty(check.GetUpdates());
        var failure = Assert.Single(failures);
        Assert.Contains("No valid version found", failure.Reason);
        Assert.Equal("", failure.RecoverUrl);
    }
}
