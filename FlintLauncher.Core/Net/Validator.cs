// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/Validator.h and net/ChecksumValidator.h.

using System.Security.Cryptography;

namespace FlintLauncher.Core.Net;

public abstract class Validator
{
    public abstract bool Init(NetRequestSpec request);

    public abstract bool Write(byte[] data);

    public abstract bool Abort();

    public abstract bool Validate(NetResponseInfo response);
}

/// <summary>Request metadata validators may inspect before the transfer starts.</summary>
public sealed class NetRequestSpec
{
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Response metadata handed to validators at finalize time.</summary>
public sealed class NetResponseInfo
{
    public required int StatusCode { get; init; }

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChecksumValidator(HashAlgorithmName algorithm, byte[]? expected = null) : Validator
{
    private IncrementalHash? hash;
    public byte[] Expected { get; set; } = expected ?? [];

    /// <summary>Port of the hex-string overload.</summary>
    public static ChecksumValidator FromHex(HashAlgorithmName algorithm, string expectedHex) => new(
        algorithm,
        expectedHex.Length == 0 ? [] : Convert.FromHexString(expectedHex));

    public override bool Init(NetRequestSpec request)
    {
        hash = IncrementalHash.CreateHash(algorithm);
        return true;
    }

    public override bool Write(byte[] data)
    {
        hash?.AppendData(data);
        return true;
    }

    public override bool Abort()
    {
        hash = null;
        return true;
    }

    public override bool Validate(NetResponseInfo response)
    {
        if (Expected.Length > 0 && !Expected.AsSpan().SequenceEqual(Hash()))
        {
            return false;
        }

        return true;
    }

    public byte[] Hash()
    {
        if (hash is null)
        {
            return [];
        }

        var result = hash.GetHashAndReset();
        hash = null;
        return result;
    }
}
