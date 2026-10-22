// SPDX-License-Identifier: GPL-3.0-only
// Port of the servers.dat NBT logic from launcher/ui/pages/instance/ServersPage.cpp
// (struct Server + parseServersDat/serializeServerDat + ServersModel::load/save_internal).
// The Qt page/model itself (ServersModel, the FS watcher, the save timer, the ping
// integration) is intentionally NOT ported — this is the clean data layer.
//
// Deviations:
//  - The C++ Server ctor indexes server["ip"]/server["name"] directly, which throws
//    (uncaught) when tags are missing; the port tolerates missing tags with "" defaults.
//  - ServersModel::load() called serversDat->at("servers").as<tag_list>() without error
//    handling; the port returns an empty list when the tag is missing or mistyped.
//  - server["icon"] truthiness (a null/empty value check) maps to "tag present and
//    non-empty"; invalid base64 yields no icon instead of partial bytes.

using FlintLauncher.Core.IO;
using FlintLauncher.Nbt;
using TruePath;

namespace FlintLauncher.Core.Minecraft.Worlds;

/// <summary>Port of Server::AcceptsTextures.</summary>
public enum AcceptsTextures
{
    Ask = 0,
    Always = 1,
    Never = 2,
}

/// <summary>One entry of the Minecraft multiplayer server list (ServersPage.cpp struct Server).</summary>
public sealed class ServerEntry
{
    /// <summary>Default like Server() with QObject::tr("Minecraft Server") (no i18n here).</summary>
    public string Name { get; set; } = "Minecraft Server";

    /// <summary>"host:port" address (Server::m_address).</summary>
    public string Address { get; set; } = "";

    public AcceptsTextures AcceptsTextures { get; set; } = AcceptsTextures.Ask;

    /// <summary>Decoded server icon bytes (Server::m_icon); null when absent.</summary>
    public byte[]? Icon { get; init; }

    public ServerEntry() { }

    public ServerEntry(string name, string address)
    {
        Name = name;
        Address = address;
    }

    /// <summary>Port of Server(nbt::tag_compound& server).</summary>
    public ServerEntry(NbtCompound server)
    {
        Address = server.TryGet<NbtString>("ip")?.Value ?? "";
        Name = server.TryGet<NbtString>("name")?.Value ?? "";
        var icon = server.TryGet<NbtString>("icon")?.Value;
        if (!string.IsNullOrEmpty(icon))
        {
            try
            {
                Icon = Convert.FromBase64String(icon);
            }
            catch (FormatException)
            {
                Icon = null;
            }
        }

        if (server.HasKey("acceptTextures", NbtTagType.Byte) &&
            server.TryGet<NbtByte>("acceptTextures") is { } acceptTextures)
        {
            AcceptsTextures = acceptTextures.Value != 0 ? AcceptsTextures.Always : AcceptsTextures.Never;
        }
    }

    /// <summary>Port of Server::serialize(nbt::tag_compound& server).</summary>
    public void Serialize(NbtCompound server)
    {
        server.Put("name", new NbtString(Name.Trim()));
        server.Put("ip", new NbtString(Address.Trim()));
        if (Icon is { Length: > 0 })
        {
            server.Put("icon", new NbtString(Convert.ToBase64String(Icon)));
        }

        if (AcceptsTextures != AcceptsTextures.Ask)
        {
            server.Put("acceptTextures", new NbtByte(AcceptsTextures == AcceptsTextures.Always ? (sbyte)1 : (sbyte)0));
        }
    }

    /// <summary>Builds the tag for this server (used by Save/Serialize).</summary>
    internal NbtCompound ToNbt()
    {
        var compound = new NbtCompound();
        Serialize(compound);
        return compound;
    }

    internal static ServerEntry FromNbt(NbtCompound compound) => new(compound);
}

/// <summary>
/// Load/save of the raw-NBT servers.dat file ("servers" list of compound tags).
/// Corresponds to parseServersDat/serializeServerDat and ServersModel::load/save_internal.
/// </summary>
public static class ServersDat
{
    /// <summary>
    /// Port of ServersModel::load + parseServersDat: reads &lt;instance&gt;/servers.dat.
    /// A missing or unreadable file, or unparseable contents, yields an empty list.
    /// </summary>
    public static List<ServerEntry> Load(AbsolutePath path)
    {
        try
        {
            return Parse(File.ReadAllBytes(path.Value));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NbtParseException)
        {
            return [];
        }
    }

    /// <summary>Parses servers.dat contents (raw NBT, no gzip — like parseServersDat).</summary>
    public static List<ServerEntry> Parse(byte[] data)
    {
        var root = NbtIo.TryReadCompound(data);
        if (root is null)
        {
            return [];
        }

        return FromCompound(root);
    }

    /// <summary>Extracts the server entries from a root compound (the "servers" tag list).</summary>
    public static List<ServerEntry> FromCompound(NbtCompound root)
    {
        var servers = new List<ServerEntry>();
        if (root.TryGet<NbtList>("servers") is not { } list)
        {
            return servers; // missing or not a list: empty (see deviations note)
        }

        foreach (var tag in list.Tags)
        {
            if (tag is NbtCompound compound)
            {
                servers.Add(new ServerEntry(compound));
            }
        }

        return servers;
    }

    /// <summary>
    /// Port of ServersModel::save_internal + serializeServerDat: builds the compound and
    /// writes it as raw NBT (atomically, like FS::write/QSaveFile).
    /// </summary>
    public static void Save(AbsolutePath path, IReadOnlyList<ServerEntry> servers)
    {
        FileOps.EnsureFilePathExists(new AbsolutePath(path));
        FileOps.Write(new AbsolutePath(path), Serialize(servers));
    }

    /// <summary>Serializes servers to the servers.dat NBT structure (raw bytes).</summary>
    public static byte[] Serialize(IReadOnlyList<ServerEntry> servers)
    {
        var outTag = new NbtCompound();
        var list = new NbtList();
        foreach (var server in servers)
        {
            list.Add(server.ToNbt());
        }

        outTag.Put("servers", list);
        return NbtIo.WriteCompoundRaw(outTag);
    }
}
