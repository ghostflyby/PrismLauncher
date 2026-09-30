// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/helpers/HashUtils.{h,cpp}: hashing helpers for the mod platforms
// (sha1/sha256/sha512/md5/md5 via the crypto stack, and the CurseForge murmur2 variant
// with its tab/LF/CR/space filter). The QtConcurrent Hasher task becomes the awaitable
// Hasher FlintTask.
using System.Globalization;
using System.Security.Cryptography;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Hashing;

/// <summary>Port of Hashing::Algorithm.</summary>
public enum Algorithm : byte
{
    Md4,
    Md5,
    Sha1,
    Sha256,
    Sha512,
    Murmur2,
    Unknown,
}

public static class HashUtils
{
    /// <summary>Port of Hashing::algorithmToString.</summary>
    public static string AlgorithmToString(Algorithm type) => type switch
    {
        Algorithm.Md4 => "md4",
        Algorithm.Md5 => "md5",
        Algorithm.Sha1 => "sha1",
        Algorithm.Sha256 => "sha256",
        Algorithm.Sha512 => "sha512",
        Algorithm.Murmur2 => "murmur2",
        _ => "unknown",
    };

    /// <summary>Port of Hashing::algorithmFromString.</summary>
    public static Algorithm AlgorithmFromString(string type) => type switch
    {
        "md4" => Algorithm.Md4,
        "md5" => Algorithm.Md5,
        "sha1" => Algorithm.Sha1,
        "sha256" => Algorithm.Sha256,
        "sha512" => Algorithm.Sha512,
        "murmur2" => Algorithm.Murmur2,
        _ => Algorithm.Unknown,
    };

    /// <summary>
    /// Port of Hashing::createHasher(file_path, provider): Modrinth uses its first declared
    /// hash type (sha512), Flame uses the CF murmur2 variant.
    /// </summary>
    public static Hasher CreateHasher(string filePath, ResourceProvider provider) => provider switch
    {
        ResourceProvider.MODRINTH => new Hasher(filePath, ProviderCapabilities.HashType(ResourceProvider.MODRINTH)[0]),
        ResourceProvider.FLAME => new Hasher(filePath, Algorithm.Murmur2),
        _ => throw new ArgumentException("[Hashing] Unrecognized mod platform!", nameof(provider)),
    };

    /// <summary>Port of Hashing::createHasher(file_path, type).</summary>
    public static Hasher CreateHasher(string filePath, string type) => new(filePath, type);

    /// <summary>Port of Hashing::hash(device, type) over raw data.</summary>
    public static string Hash(byte[] data, Algorithm type) => type switch
    {
        // CF-specific: must match the (weird) CF implementation
        Algorithm.Murmur2 => Murmur2.Hash(data, ShouldFilterOut).ToString(CultureInfo.InvariantCulture),
        Algorithm.Unknown => "",
        _ => ComputeHash(data, type),
    };

    /// <summary>Port of Hashing::hash(fileName, type): hash a file's content.</summary>
    public static string HashFile(string fileName, Algorithm type) => Hash(File.ReadAllBytes(fileName), type);

    /// <summary>Port of Hashing::hash(device, type) streamed from an open file.</summary>
    public static string HashStream(Stream stream, Algorithm type)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Hash(buffer.ToArray(), type);
    }

    private static bool ShouldFilterOut(byte c) => c is 9 or 10 or 13 or 32;

    /// <summary>
    /// Deviation note: .NET has no managed MD4 (Qt's QCryptographicHash::Md4); requesting it
    /// hashes with MD5. The launcher only ever requests sha1/sha512/murmur2 through this
    /// layer, so the substitution is inert.
    /// </summary>
    private static string ComputeHash(byte[] data, Algorithm type) => type switch
    {
        Algorithm.Sha1 => Convert.ToHexStringLower(SHA1.HashData(data)),
        Algorithm.Sha256 => Convert.ToHexStringLower(SHA256.HashData(data)),
        Algorithm.Sha512 => Convert.ToHexStringLower(SHA512.HashData(data)),
        _ => Convert.ToHexStringLower(MD5.HashData(data)), // Md4 (see note) and Md5
    };

    public static string Hash(byte[] data, string type) => Hash(data, AlgorithmFromString(type));
}

/// <summary>Port of Hashing::Hasher: a Task that computes a file hash off the caller's flow.</summary>
public sealed class Hasher : FlintTask
{
    private string _result = "";

    public Hasher(string filePath, Algorithm alg)
    {
        Path = filePath;
        Alg = alg;
    }

    public Hasher(string filePath, string alg)
        : this(filePath, HashUtils.AlgorithmFromString(alg))
    {
    }

    public string Path { get; }

    public Algorithm Alg { get; }

    public string GetResult() => _result;

    public string GetPath() => Path;

    /// <summary>Port of the resultsReady(QString) signal.</summary>
    public event Action<string>? ResultsReady;

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _result = HashUtils.HashFile(Path, Alg);
        if (_result.Length == 0)
        {
            EmitFailed("Empty hash!");
            return Task.CompletedTask;
        }
        ResultsReady?.Invoke(_result);
        EmitSucceeded();
        return Task.CompletedTask;
    }
}
