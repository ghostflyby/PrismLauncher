// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
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
    private IncrementalHash? _hash;
    public byte[] Expected { get; set; } = expected ?? [];

    /// <summary>Port of the hex-string overload.</summary>
    public static ChecksumValidator FromHex(HashAlgorithmName algorithm, string expectedHex) => new(
        algorithm,
        expectedHex.Length == 0 ? [] : Convert.FromHexString(expectedHex));

    public override bool Init(NetRequestSpec request)
    {
        _hash = IncrementalHash.CreateHash(algorithm);
        return true;
    }

    public override bool Write(byte[] data)
    {
        _hash?.AppendData(data);
        return true;
    }

    public override bool Abort()
    {
        _hash = null;
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
        if (_hash is null)
        {
            return [];
        }

        var result = _hash.GetHashAndReset();
        _hash = null;
        return result;
    }
}
