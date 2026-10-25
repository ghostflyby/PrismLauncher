// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for Worlds/ServersDat.cs: synthesize a servers.dat, round-trip it, edit it,
// save, and reload — mirroring the ServersPage.cpp parse/serialize logic.

using FlintLauncher.Core.Minecraft.Worlds;
using FlintLauncher.Nbt;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class ServersDatTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "flint-servers-" + Guid.NewGuid().ToString("N"));

    public ServersDatTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static List<ServerEntry> SampleServers()
    {
        return
        [
            new ServerEntry("Local Host", "127.0.0.1:25565")
            {
                Icon = [1, 2, 3, 4],
                AcceptsTextures = AcceptsTextures.Never,
            },
            new ServerEntry("Hypixel  \u597d", "mc.hypixel.net"),
        ];
    }

    [Fact]
    public void Serialize_Parse_RoundTrip_PreservesFields()
    {
        var servers = SampleServers();

        var bytes = ServersDat.Serialize(servers);
        var parsed = ServersDat.Parse(bytes);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Local Host", parsed[0].Name);
        Assert.Equal("127.0.0.1:25565", parsed[0].Address);
        Assert.Equal(AcceptsTextures.Never, parsed[0].AcceptsTextures);
        Assert.NotNull(parsed[0].Icon);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed[0].Icon);

        Assert.Equal("Hypixel  \u597d", parsed[1].Name);
        Assert.Equal("mc.hypixel.net", parsed[1].Address);
        Assert.Equal(AcceptsTextures.Ask, parsed[1].AcceptsTextures);
        Assert.Null(parsed[1].Icon);
    }

    [Fact]
    public void Serialize_IsByteStable()
    {
        var servers = SampleServers();
        var once = ServersDat.Serialize(servers);
        var twice = ServersDat.Serialize(ServersDat.Parse(once));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Serialize_RawNbt_NoGzipMagic()
    {
        var bytes = ServersDat.Serialize(SampleServers());
        Assert.True(bytes[0] != 0x1F || bytes[1] != 0x8B, "servers.dat is stored raw, not gzipped");
        Assert.Equal((byte)NbtTagType.Compound, bytes[0]);
    }

    [Fact]
    public void Serialize_TrimsNameAndAddress()
    {
        // Server::serialize stores m_name.trimmed()/m_address.trimmed()
        var bytes = ServersDat.Serialize([new ServerEntry("  spaced  ", "  1.2.3.4  ")]);
        var parsed = ServersDat.Parse(bytes);
        Assert.Equal("spaced", parsed[0].Name);
        Assert.Equal("1.2.3.4", parsed[0].Address);
    }

    [Fact]
    public void Serialize_OmitsOptionalFieldsWhenDefaulted()
    {
        var bytes = ServersDat.Serialize([new ServerEntry("plain", "example.com")]);
        var root = NbtIo.TryReadCompound(bytes);
        Assert.NotNull(root);
        var serverTag = (NbtCompound)((NbtList)root["servers"])[0];
        Assert.False(serverTag.HasKey("icon"));
        Assert.False(serverTag.HasKey("acceptTextures"));
        Assert.Equal(2, serverTag.Count); // only name and ip
    }

    [Fact]
    public void Parse_AcceptsTexturesByte()
    {
        // hand-built entry like the C++ parser reads it
        var server = new NbtCompound();
        server.Put("name", new NbtString("tex"));
        server.Put("ip", new NbtString("tex.example.com"));
        server.Put("acceptTextures", new NbtByte(1));
        var root = new NbtCompound();
        var list = new NbtList();
        list.Add(server);
        root.Put("servers", list);

        var parsed = ServersDat.FromCompound(root);
        var entry = Assert.Single(parsed);
        Assert.Equal(AcceptsTextures.Always, entry.AcceptsTextures);

        server.Put("acceptTextures", new NbtByte(0));
        Assert.Equal(AcceptsTextures.Never, new ServerEntry(server).AcceptsTextures);
    }

    [Fact]
    public void Parse_GarbageData_ReturnsEmpty()
    {
        Assert.Empty(ServersDat.Parse([1, 2, 3, 4, 5]));
        Assert.Empty(ServersDat.Parse([]));
    }

    [Fact]
    public void Parse_MissingServersTag_ReturnsEmpty()
    {
        var root = new NbtCompound();
        root.Put("something", new NbtInt(3));
        Assert.Empty(ServersDat.FromCompound(root));
    }

    [Fact]
    public void Parse_NonCompoundEntries_AreSkipped()
    {
        var root = new NbtCompound();
        var list = new NbtList();
        list.Add(new NbtString("not a compound"));
        root.Put("servers", list);

        Assert.Empty(ServersDat.FromCompound(root));
    }

    [Fact]
    public void Save_Load_RoundTrip_ThenEdit()
    {
        var path = new AbsolutePath(Path.Combine(_tempDir, "servers.dat"));
        ServersDat.Save(path, SampleServers());
        Assert.True(path.ExistsFile());

        var loaded = ServersDat.Load(path);
        Assert.Equal(2, loaded.Count);

        // edit: flip the first server's texture preference and reload
        loaded[0].AcceptsTextures = AcceptsTextures.Always;
        loaded.Add(new ServerEntry("New Server", "new.example.com:25565"));
        ServersDat.Save(path, loaded);

        var reloaded = ServersDat.Load(path);
        Assert.Equal(3, reloaded.Count);
        Assert.Equal(AcceptsTextures.Always, reloaded[0].AcceptsTextures);
        Assert.Equal("New Server", reloaded[2].Name);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(ServersDat.Load(new AbsolutePath(Path.Combine(_tempDir, "does-not-exist.dat"))));
    }
}
