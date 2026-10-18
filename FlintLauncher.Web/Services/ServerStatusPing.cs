// SPDX-License-Identifier: GPL-3.0-only
// Minimal Minecraft server status ping for the servers-page surface: SRV resolution
// (launcher/ui/pages/instance/McResolver.cpp semantics over the core SrvResolver) followed by
// the tiny status handshake of launcher/ui/pages/instance/McClient.cpp (handshake packet +
// status request + length/varInt-framed JSON reply).
//
// Deviations from the C++:
//  - the C++ resolves the A record through QHostInfo before connecting; .NET's
//    Dns.GetHostAddressesAsync fills that role (numeric hosts skip DNS entirely).
//  - protocol version 763 (1.20.1) is hardcoded in McClient::sendRequest and kept here.
//  - timeouts: the Qt version relies on socket errors; this port bounds the whole exchange
//    with a cancellation token (PingTimeoutMs) and fails with a clear message.
//  - the online-players extraction of ServerPingTask.cpp (getOnlinePlayers) becomes part of
//    the parse step; the summary fields are returned to the caller.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Launch;

namespace FlintLauncher.Web.Services;

/// <summary>The parsed status reply (McClient::succeeded payload + the resolution outcome).</summary>
public sealed record ServerPingResult(
    string ResolvedIp,
    int ResolvedPort,
    int OnlinePlayers,
    int MaxPlayers,
    string? Version,
    string? Motd,
    long LatencyMs);

public static class ServerStatusPing
{
    /// <summary>McClient's hardcoded handshake protocol version (763 = 1.20.1).</summary>
    private const int ProtocolVersion = 763;

    public const int PingTimeoutMs = 5000;

    /// <summary>
    /// Port of ServerPingTask::executeTask: resolve the address (SRV when the host is a
    /// name, direct otherwise) and run the status handshake against it.
    /// </summary>
    public static async Task<ServerPingResult> PingAsync(string serverAddress,
        CancellationToken cancellationToken = default)
    {
        // ServersPage.cpp: MinecraftTarget::parse splits "host:port" (default 25565)
        var target = MinecraftTarget.Parse(serverAddress, useWorld: false);

        var ip = await ResolveIpAsync(target.Address, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(PingTimeoutMs));
        var json = await RequestStatusAsync(ip, target.Port, target.Address, timeout.Token)
            .ConfigureAwait(false);
        stopwatch.Stop();

        return ParseStatus(ip, target.Port, json, stopwatch.ElapsedMilliseconds);
    }

    // --------------------------------------------------------------------- resolution

    /// <summary>Port of McResolver: SRV lookup under _minecraft._tcp (rewrites host+port when
    /// records exist), then the A lookup; numeric hosts skip DNS entirely.</summary>
    private static async Task<string> ResolveIpAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var numeric))
        {
            return numeric.ToString();
        }

        try
        {
            var record = await SrvResolver
                .QueryFirstAsync($"_minecraft._tcp.{host}", serverEndpoint: null, cancellationToken)
                .ConfigureAwait(false);
            if (record is { } srv)
            {
                host = srv.Target;
            }

            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                // "No A entries found for domain" (McResolver::pingWithDomainA)
                throw new InvalidOperationException($"no A records for '{host}'");
            }

            return addresses[0].ToString();
        }
        catch (Exception e) when (e is SocketException or Win32Exception)
        {
            // "A record lookup failed" — resolution errors surface as the ping failure
            throw new InvalidOperationException($"could not resolve '{host}': {e.Message}", e);
        }
    }

    // ---------------------------------------------------------------- status handshake

    /// <summary>Port of McClient::getStatusData/sendRequest/readRawResponse/parseResponse.</summary>
    private static async Task<string> RequestStatusAsync(string ip, int port, string domain,
        CancellationToken cancellationToken)
    {
        // an IPv6 socket with DualMode covers both literal and resolved v4/v6 addresses
        using var socket = new TcpClient(AddressFamily.InterNetworkV6);
        socket.Client.DualMode = true;
        try
        {
            await socket.ConnectAsync(ip, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
        {
            throw new InvalidOperationException($"could not connect to {ip}:{port}", e);
        }

        await using var stream = socket.GetStream();

        // handshake packet: id 0x00, protocol, server address, port, next state 1
        var handshake = new MemoryStream();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, ProtocolVersion);
        WriteString(handshake, domain);
        WriteUInt16(handshake, (ushort)port);
        WriteVarInt(handshake, 0x01);
        await WritePacketAsync(stream, handshake.ToArray(), cancellationToken).ConfigureAwait(false);

        // status request: id 0x00 only
        var status = new MemoryStream();
        WriteVarInt(status, 0x00);
        await WritePacketAsync(stream, status.ToArray(), cancellationToken).ConfigureAwait(false);

        // reply: varInt packet length, then the packet
        var packetLength = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
        if (packetLength is < 0 or > 2_097_151)
        {
            throw new InvalidOperationException($"invalid packet length {packetLength}");
        }

        var packet = await ReadExactlyAsync(stream, packetLength, cancellationToken).ConfigureAwait(false);

        // packet: varInt id (0x00) + varInt string length + the JSON string
        var offset = 0;
        var packetId = ReadVarInt(packet, ref offset);
        if (packetId != 0x00)
        {
            throw new InvalidOperationException($"packet id does not match 0x00 (got 0x{packetId:x})");
        }

        var jsonLength = ReadVarInt(packet, ref offset);
        return Encoding.UTF8.GetString(packet, offset, Math.Min(jsonLength, packet.Length - offset));
    }

    /// <summary>Port of ServerPingTask::getOnlinePlayers + the McClient::succeeded payload:
    /// extracts players.online/max, version.name and the motd text when present.</summary>
    private static ServerPingResult ParseStatus(string ip, int port, string json, long latencyMs)
    {
        ServerStatusJson? status;
        try
        {
            status = JsonSerializer.Deserialize(json, ServerPingJsonContext.Default.ServerStatusJson);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("server sent an invalid status reply", e);
        }

        return new ServerPingResult(ip, port,
            status?.Players?.Online ?? 0,
            status?.Players?.Max ?? 0,
            status?.Version?.Name,
            status?.Description,
            latencyMs);
    }

    // ------------------------------------------------------------------- varInt framing

    /// <summary>Port of McClient::writeVarInt (wiki.vg VarInt encoding).</summary>
    internal static void WriteVarInt(Stream stream, int value)
    {
        while ((value & ~0x7F) != 0)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] packet,
        CancellationToken cancellationToken)
    {
        using var framed = new MemoryStream();
        WriteVarInt(framed, packet.Length);
        framed.Write(packet);
        await stream.WriteAsync(framed.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of McClient::readVarInt over the wire (one byte at a time).</summary>
    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var value = 0;
        var position = 0;
        while (position < 32)
        {
            await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            var current = buffer[0];
            value |= (current & 0x7F) << position;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            position += 7;
        }

        throw new InvalidOperationException("varint is too big");
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("the server closed the connection");
            }

            offset += read;
        }
    }

    /// <summary>Port of McClient::readVarInt over an in-memory packet (buffer + offset).</summary>
    private static int ReadVarInt(byte[] buffer, ref int offset)
    {
        var value = 0;
        var position = 0;
        while (position < 32)
        {
            if (offset >= buffer.Length)
            {
                throw new InvalidOperationException("no more bytes to read");
            }

            var current = buffer[offset++];
            value |= (current & 0x7F) << position;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            position += 7;
        }

        throw new InvalidOperationException("varint is too big");
    }
}

// ----------------------------------------------------------------------------------------
// The status-reply wire (read-only). The description member is the raw-JSON text component:
// a string reads verbatim, an object reads its string "text" member, anything else keeps
// its raw JSON text (the former d.ToJsonString() fallback).

internal sealed record ServerStatusJson
{
    /// <summary>Non-object members read null (the former `is JsonObject` probes) so a
    /// broken section degrades to its defaults instead of failing the whole reply.</summary>
    [JsonConverter(typeof(ServerStatusPlayersConverter))]
    public ServerStatusPlayersJson? Players { get; set; }

    [JsonConverter(typeof(ServerStatusVersionConverter))]
    public ServerStatusVersionJson? Version { get; set; }

    [JsonConverter(typeof(MotdConverter))]
    public string? Description { get; set; }
}

internal sealed record ServerStatusPlayersJson
{
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int Online { get; set; }

    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int Max { get; set; }
}

internal sealed record ServerStatusVersionJson
{
    [JsonConverter(typeof(QtCoercingNullableStringConverter))]
    public string? Name { get; set; }
}

internal sealed class ServerStatusPlayersConverter : JsonConverter<ServerStatusPlayersJson?>
{
    public override ServerStatusPlayersJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.StartObject
            ? JsonSerializer.Deserialize(ref reader, ServerPingJsonContext.Default.ServerStatusPlayersJson)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, ServerStatusPlayersJson? value, JsonSerializerOptions options) =>
        writer.WriteStartObject();
}

internal sealed class ServerStatusVersionConverter : JsonConverter<ServerStatusVersionJson?>
{
    public override ServerStatusVersionJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.StartObject
            ? JsonSerializer.Deserialize(ref reader, ServerPingJsonContext.Default.ServerStatusVersionJson)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, ServerStatusVersionJson? value, JsonSerializerOptions options) =>
        writer.WriteStartObject();
}

internal sealed class MotdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        var node = JsonNode.Parse(ref reader);
        if (node is JsonObject motd && motd["text"] is JsonValue text &&
            text.TryGetValue<string>(out var motdText))
        {
            return motdText;
        }

        return node?.ToJsonString();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServerStatusJson))]
[JsonSerializable(typeof(ServerStatusPlayersJson))]
[JsonSerializable(typeof(ServerStatusVersionJson))]
internal sealed partial class ServerPingJsonContext : JsonSerializerContext;
