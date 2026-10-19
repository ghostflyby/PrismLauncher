// SPDX-License-Identifier: GPL-3.0-only
// Strongly-typed JSON file infrastructure (GLOBAL §1): one instance per file, load-or-
// default, atomic write, and a stateVersion evolution hook. Consumers (launcher-state.json,
// migration/*.json, instance-state.json, later flint.json) pass the source-generated
// JsonTypeInfo<T> from FlintJsonContext — reflection-based serialization never enters the
// picture (Native AOT red line: a missing root registration is a runtime failure, not a
// compile error).

using System.Diagnostics.CodeAnalysis;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core;

/// <summary>Marker for state records persisted with a <c>stateVersion</c> discriminator (S5
/// launcher-state.json, D4 instance-state.json). The property must be serialized so the
/// evolution gate can read the on-disk version.</summary>
public interface IStateVersioned
{
    int StateVersion { get; }
}

/// <summary>A single strongly-typed JSON file. Not thread-safe: callers own serialization
/// (the runtime keeps one writer per domain, e.g. LauncherRuntime.InstanceLock).</summary>
public sealed class TypedJsonFile<T>(string filePath, JsonTypeInfo<T> typeInfo)
    where T : class
{
    public string FilePath { get; } = filePath;

    /// <summary>load-or-default: a missing (or unreadable/corrupt) file yields the factory
    /// default. A corrupt payload is preserved next to the file as <c>&lt;name&gt;.corrupt</c>
    /// (previous backup replaced) — defaulting must not silently destroy the evidence.
    ///
    /// <paramref name="mergeWithDefault"/> is the record-merge helper (C6/C7 fold shape):
    /// it receives the loaded record and folds it over a fresh default so members added
    /// since the file was written take their current default — with-expressions only, no
    /// reflection, e.g. <c>loaded => loaded with { NewField = loaded.NewField }</c> when the
    /// default already lives in the record's property initializer.</summary>
    public T LoadOrDefault(Func<T> defaultFactory, Func<T, T>? mergeWithDefault = null)
    {
        var value = TryLoad(out var loaded) ? loaded : defaultFactory();
        return mergeWithDefault is null ? value : mergeWithDefault(value);
    }

    /// <summary>Load, or null when the file is missing/unreadable/corrupt. The corrupt-file
    /// evidence handling matches <see cref="LoadOrDefault"/>; callers that model "absent"
    /// as null (the migration decision store) use this instead of a null-returning factory.</summary>
    public T? LoadOrNull() => TryLoad(out var loaded) ? loaded : null;

    /// <summary>Atomic write: serialize to a temp file in the target directory, then rename
    /// over the target (rename-within-directory is atomic on all supported filesystems).
    /// Missing parent directories are created. On unix, <paramref name="unixFileMode"/> is
    /// applied to the temp file BEFORE the payload is written so restrictive payloads
    /// (accounts/secrets, 0600) never appear world-readable, not even transiently. No
    /// fsync: the guarantee is rename atomicity, matching the C++ QSaveFile behavior this
    /// replaces.</summary>
    public void Save(T value, UnixFileMode? unixFileMode = null)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = File.Create(temp))
            {
                // narrow the permissions BEFORE the payload is written: the temp file lives
                // for the duration of the serialize, and a 0600 payload (accounts/secrets)
                // must never be world-readable even for that window
                if (unixFileMode is { } mode && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, mode);
                }

                JsonSerializer.Serialize(stream, value, typeInfo);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // the rename either took the temp with it or the next Save cleans up
                }
            }
        }
    }

    private bool TryLoad([NotNullWhen(true)] out T? value)
    {
        value = null;
        if (!File.Exists(FilePath))
        {
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or SecurityException or NotSupportedException)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(json, typeInfo);
            return value is not null;
        }
        catch (JsonException)
        {
            try
            {
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or SecurityException or NotSupportedException)
            {
                // the backup is best-effort; the default still applies
            }

            return false;
        }
    }
}

/// <summary>The stateVersion evolution half of the infrastructure (GLOBAL §1; S5/D4 share
/// it). <see cref="TypedJsonFileVersioning.LoadEvolved{U}"/> gates the caller's migration function
/// on the on-disk version vs the current default's version — the current default record IS
/// the schema version, so the caller never states the number twice.</summary>
public static class TypedJsonFileVersioning
{
    /// <summary>Load with evolution: a missing/corrupt file yields
    /// <paramref name="currentDefault"/> untouched; a payload whose StateVersion equals the
    /// default's is returned as-is; anything else (older schema, or a newer schema from a
    /// downgrade) is handed to <paramref name="evolve"/>(onDiskVersion, payload) which owns
    /// both the upgrade and the downgrade policy. <paramref name="mergeWithDefault"/>, when
    /// given, folds EVERY loaded payload over the current defaults BEFORE the gate (the
    /// C6/C7 fold shape: members absent from an older/partial payload take their property
    /// defaults) — this is the single stateVersion gate; do not fork a second one.</summary>
    public static TU LoadEvolved<TU>(this TypedJsonFile<TU> file, Func<TU> currentDefault,
        Func<int, TU, TU> evolve, Func<TU, TU>? mergeWithDefault = null)
        where TU : class, IStateVersioned
    {
        var latest = currentDefault();
        var loaded = file.LoadOrDefault(() => latest, mergeWithDefault);
        return loaded.StateVersion == latest.StateVersion ? loaded : evolve(loaded.StateVersion, loaded);
    }
}
