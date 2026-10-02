// SPDX-License-Identifier: GPL-3.0-only
// The secret store model (CONFIG C4 + GLOBAL G4): launcher secrets (accounts tokens, proxy
// password, API keys) leave the config world entirely — code reads and writes them by NAME
// through <see cref="ISecretStore"/>, the config format carries no secret fields. The
// transitional backend is <see cref="FileSecretStore"/>: <data-root>/secrets.json, 0600 on
// unix, atomic writes via the TypedJsonFile mechanism. Future backends (Keychain/DPAPI/
// libsecret) implement the same interface without touching the consumers.
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.Secrets;

/// <summary>Named secret read/write (C4: "ISecretStore（Get/Set/Delete by name）").</summary>
public interface ISecretStore
{
    /// <summary>The stored secret, or null when the name is unknown.</summary>
    string? Get(string name);

    /// <summary>Store (or replace) the secret under <paramref name="name"/> and persist.</summary>
    void Set(string name, string value);

    /// <summary>Remove the secret; false when it was not stored.</summary>
    bool Delete(string name);
}

/// <summary>The on-disk payload of the transitional backend: one flat name → secret map.
/// Registered in FlintJsonContext with the rest of the TypedJsonFile roots.</summary>
public sealed record SecretsFile
{
    public const int CurrentVersion = 1;

    public int StateVersion { get; init; } = CurrentVersion;

    public Dictionary<string, string> Secrets { get; init; } = new();
}

/// <summary>The transitional file backend (C4): &lt;data-root&gt;/secrets.json, 0600 on unix,
/// atomic temp+rename writes. Not thread-safe across instances; the host keeps exactly one
/// and serializes through it (a single file, a single writer).</summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly TypedJsonFile<SecretsFile> _file;

    public FileSecretStore(string filePath, JsonTypeInfo<SecretsFile> typeInfo)
    {
        _file = new TypedJsonFile<SecretsFile>(filePath, typeInfo);
    }

    /// <summary>The backing file (data-root/secrets.json for the host's instance).</summary>
    public string FilePath => _file.FilePath;

    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Load().Secrets.GetValueOrDefault(name);
    }

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var current = Load();
        var secrets = new Dictionary<string, string>(current.Secrets, StringComparer.Ordinal)
        {
            [name] = value,
        };
        _file.Save(current with { Secrets = secrets }, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var current = Load();
        if (!current.Secrets.Remove(name))
        {
            return false;
        }
        _file.Save(current, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return true;
    }

    /// <summary>load-or-default with the record-merge fold: the source-generated deserializer
    /// does not honor property initializers for absent members, so a payload without the
    /// secrets map would surface null instead of an empty dictionary.</summary>
    private SecretsFile Load() =>
        _file.LoadOrDefault(() => new SecretsFile(),
            merged => merged with
            {
                Secrets = merged.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });
}
