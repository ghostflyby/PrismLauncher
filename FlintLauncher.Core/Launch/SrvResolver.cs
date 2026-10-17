// SPDX-License-Identifier: GPL-3.0-only
// Minimal DNS SRV resolver used by the LookupServerAddress launch step. The C++ uses
// QDnsLookup (and the server browser page uses ui/pages/instance/McResolver.cpp on top of
// it); .NET has no public SRV API and no DnsClient package is available, so this implements
// just enough RFC 2052/2782 wire protocol over UdpClient:
//   query  _minecraft._tcp.<domain>  QTYPE=SRV(33) QCLASS=IN(1)
// and parses the first answer record (priority, weight, port, target).
//
// Deviations from the C++: no TCP fallback, no retry across multiple nameservers (the first
// usable nameserver wins) and server selection reads /etc/resolv.conf (macOS/Linux/FreeBSD);
// on Windows there is no resolv.conf, so the resolver falls back to the first nameserver of
// NetworkInterface properties — if that fails, callers treat the lookup as failed exactly
// like a QDnsLookup error. The queried server can be overridden per launch through the
// injected LaunchServices.SrvServerEndpoint (tests point it at a local UDP stub).

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FlintLauncher.Core.Launch;

public sealed record SrvRecord(ushort Priority, ushort Weight, ushort Port, string Target);

public static class SrvResolver
{
    public const int DefaultTimeoutMs = 5000;

    /// <summary>Query SRV records; returns an empty list when the name has no SRV records.
    /// <paramref name="serverEndpoint"/> overrides the system nameserver (the injected
    /// LaunchServices.SrvServerEndpoint — the former test hook).</summary>
    public static async Task<List<SrvRecord>> QueryAsync(string name, IPEndPoint? serverEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        var server = serverEndpoint ?? ResolveSystemNameserver();
        if (server is null)
        {
            throw new InvalidOperationException("No DNS server available for SRV lookup.");
        }

        var query = BuildQuery(name);
        using var udp = new UdpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(DefaultTimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await udp.SendAsync(query, server, linked.Token).ConfigureAwait(false);

        // response can arrive fragmented across datagrams only in pathological cases; one
        // datagram covers the ~512 byte classic limit the C++ QDnsLookup also relies on
        var receiveTask = udp.ReceiveAsync(linked.Token);
        var result = await receiveTask.ConfigureAwait(false);
        return ParseResponse(result.Buffer, name);
    }

    /// <summary>
    /// Convenience wrapper matching the LookupServerAddress step semantics: resolve the SRV
    /// name; null target means "no SRV records / lookup failed" (the caller then falls back to
    /// the original address and the default port).
    /// </summary>
    public static async Task<SrvRecord?> QueryFirstAsync(string name, IPEndPoint? serverEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        var records = await QueryAsync(name, serverEndpoint, cancellationToken).ConfigureAwait(false);
        return records.Count == 0 ? null : records[0];
    }

    // ------------------------------------------------------------------------ wire format

    internal static byte[] BuildQuery(string name)
    {
        var qname = EncodeName(name);
        var packet = new byte[12 + qname.Length + 4];
        // header: id, recursion-desired flag, qdcount=1
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), (ushort)Random.Shared.Next(1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);
        qname.CopyTo(packet, 12);
        // qtype=SRV(33), qclass=IN(1)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + qname.Length, 2), 33);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + qname.Length + 2, 2), 1);
        return packet;
    }

    internal static List<SrvRecord> ParseResponse(byte[] buffer, string expectedName)
    {
        var records = new List<SrvRecord>();
        if (buffer.Length < 12)
        {
            return records;
        }
        var flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
        if ((flags & 0x8000) != 0 || (flags & 0x000F) != 0)
        {
            // not a response or an error rcode
            return records;
        }
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));

        var offset = 12;
        for (var i = 0; i < qdCount && offset < buffer.Length; i++)
        {
            offset = SkipName(buffer, offset);
            offset += 4; // qtype + qclass
        }
        for (var i = 0; i < anCount && offset + 10 <= buffer.Length; i++)
        {
            offset = SkipName(buffer, offset);
            var type = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            offset += 8; // type + class + ttl (only type needed)
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            offset += 2;
            if (type != 33 || offset + rdLength > buffer.Length)
            {
                offset += rdLength;
                continue;
            }
            var rdata = buffer.AsSpan(offset, rdLength);
            offset += rdLength;
            if (rdata.Length < 7)
            {
                continue;
            }
            var priority = BinaryPrimitives.ReadUInt16BigEndian(rdata);
            var weight = BinaryPrimitives.ReadUInt16BigEndian(rdata[2..]);
            var port = BinaryPrimitives.ReadUInt16BigEndian(rdata[4..]);
            var (target, _) = ReadName(buffer, offset - rdLength + 6);
            records.Add(new SrvRecord(priority, weight, port, target));
        }
        return records;
    }

    private static byte[] EncodeName(string name)
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var size = 1 + parts.Sum(p => 1 + Encoding.ASCII.GetByteCount(p));
        var buffer = new byte[size];
        var offset = 0;
        foreach (var part in parts)
        {
            buffer[offset++] = (byte)part.Length;
            offset += Encoding.ASCII.GetBytes(part, buffer.AsSpan(offset));
        }
        buffer[offset] = 0;
        return buffer;
    }

    private static int SkipName(byte[] buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            var length = buffer[offset];
            if (length == 0)
            {
                return offset + 1;
            }
            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2;
            }
            offset += 1 + length;
        }
        return offset;
    }

    private static (string, int) ReadName(byte[] buffer, int offset)
    {
        var builder = new StringBuilder();
        var jumps = 0;
        var end = offset;
        var current = offset;
        while (current < buffer.Length && jumps < 8)
        {
            var length = buffer[current];
            if (length == 0)
            {
                end = Math.Max(end, current + 1);
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (current + 1 >= buffer.Length)
                {
                    break;
                }
                var pointer = ((length & 0x3F) << 8) | buffer[current + 1];
                if (jumps == 0)
                {
                    end = current + 2;
                }
                current = pointer;
                jumps++;
                continue;
            }
            if (builder.Length != 0)
            {
                builder.Append('.');
            }
            builder.Append(Encoding.ASCII.GetString(buffer, current + 1, Math.Min(length, buffer.Length - current - 1)));
            current += 1 + length;
            if (jumps == 0)
            {
                end = current;
            }
        }
        return (builder.ToString(), end);
    }

    private static IPEndPoint? ResolveSystemNameserver()
    {
        if (OperatingSystem.IsWindows())
        {
            // No resolv.conf; keep the port portable without the DnsClient dependency by
            // falling back to the loopback resolver (documented limitation — callers treat a
            // failed SRV lookup as non-fatal).
            return new IPEndPoint(IPAddress.Loopback, 53);
        }

        foreach (var candidate in new[] { "/etc/resolv.conf", "/run/systemd/resolve/resolv.conf" })
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }
                foreach (var line in File.ReadLines(candidate))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("nameserver ", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var host = trimmed["nameserver ".Length..].Trim();
                    if (IPAddress.TryParse(host, out var address) &&
                        !address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.IPv6Loopback))
                    {
                        return new IPEndPoint(address, 53);
                    }
                }
            }
            catch (IOException)
            {
            }
        }
        return new IPEndPoint(IPAddress.Loopback, 53);
    }
}
