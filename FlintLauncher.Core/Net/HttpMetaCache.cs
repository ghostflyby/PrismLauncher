// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/HttpMetaCache.{h,cpp}: on-disk download cache index. The index file
// format is kept byte-compatible with PrismLauncher's "metacache" JSON (version "1"): the
// MetaCacheFileJson shape deserializes/serializes through the MetaCacheJsonContext, and the
// writer clones the context options with Qt's relaxed encoder (QJsonDocument leaves unicode
// alone and only escapes what it must) so the bytes stay identical.

using System.Security;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using TruePath;

namespace FlintLauncher.Core.Net;

/// <summary>The metacache wire shape: {"version":"1","entries":[...]} — the coercing
/// converters keep the QJsonValue fallbacks of the former readers (junk timestamps/ages
/// truncate to 0, junk strings become "").</summary>
public sealed record MetaCacheFileJson(
    string? Version,
    List<MetaCacheEntryJson>? Entries);

public sealed record MetaCacheEntryJson(
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? Base,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? Path,
    [property: JsonPropertyName("md5sum")]
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? Md5Sum,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? Etag,
    [property: JsonConverter(typeof(QtCoercingLongConverter))]
    long LastChangedTimestamp,
    [property: JsonConverter(typeof(QtCoercingStringConverter))]
    string? RemoteChangedTimestamp,
    bool? Eternal,
    [property: JsonConverter(typeof(QtCoercingLongConverter))]
    long? CurrentAge,
    [property: JsonConverter(typeof(QtCoercingLongConverter))]
    long? MaxAge);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MetaCacheFileJson))]
internal sealed partial class MetaCacheJsonContext : JsonSerializerContext;

/// <summary>The writer options: the context's own, cloned with Qt's relaxed JSON escaping
/// (the metacache must stay byte-compatible with PrismLauncher's ledger).</summary>
internal static class MetaCacheJson
{
    public static JsonSerializerOptions WriterOptions { get; } =
        new(MetaCacheJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

    /// <summary>The writer's source-generated type info: a context bound to
    /// <see cref="WriterOptions"/>, so the AOT-safe serialize overload keeps the Qt encoder
    /// instead of falling back to reflection.</summary>
    private static readonly MetaCacheJsonContext WriterContext = new(WriterOptions);

    public static JsonTypeInfo<MetaCacheFileJson> WriterTypeInfo => WriterContext.MetaCacheFileJson;
}

public sealed class MetaEntry
{
    /// <summary>The cache whose mount table resolves <see cref="GetFullPath"/> (E4: the
    /// base path is NOT a field on the entry — the storage layer owns the baseId → root
    /// table and the key → path resolution; the disk format never carried it anyway).</summary>
    private HttpMetaCache? _owner;

    public string BaseId { get; set; } = "";

    public string RelativePath { get; set; } = "";

    public string Md5Sum { get; set; } = "";

    public string Etag { get; set; } = "";

    public long LocalChangedTimestamp { get; set; }

    /// <summary>RFC 2822 encoded time, as sent by the remote.</summary>
    public string RemoteChangedTimestamp { get; set; } = "";

    public long CurrentAge { get; set; }

    public long MaxAge { get; set; }

    public bool IsEternal { get; set; }

    public bool IsStale { get; set; } = true;

    /// <summary>Full path of the cached file: resolved LIVE through the owning cache's
    /// mount table (E4 — remounting a base re-points every entry with it). Fails fast when
    /// the entry has no owning cache or its base is not registered — the silent fallback
    /// to a process-CWD-relative path is exactly the E6 defect shape (② lazy bases → CWD
    /// landing) and must not come back through a side door.</summary>
    public string GetFullPath()
    {
        var cache = this._owner ?? throw new InvalidOperationException(
            "meta entry has no owning cache — resolve it through HttpMetaCache.ResolveEntry");
        var basePath = cache.GetBasePath(BaseId);
        return basePath.Length != 0
            ? Path.Combine(basePath, RelativePath)
            : throw new InvalidOperationException(
                $"base '{BaseId}' is not registered on the owning meta cache — the composition root must register it");
    }

    public bool IsExpired(long offset) => !IsEternal && CurrentAge >= MaxAge - offset;

    internal void SetOwner(HttpMetaCache? owner) => this._owner = owner;
}

public sealed class HttpMetaCache(string? path = null) : IDisposable
{
    private readonly Lock _lock = new();

    private readonly Dictionary<string, Dictionary<string, MetaEntry>> _entries =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _basePaths = new(StringComparer.Ordinal);
    private readonly string? _indexFile = string.IsNullOrEmpty(path) ? null : path;
    private Timer? _saveTimer;

    public void Dispose()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;
        SaveNow();
    }

    public void AddBase(string baseId, string basePath)
    {
        lock (_lock)
        {
            if (!_basePaths.TryAdd(baseId, basePath))
            {
                return;
            }

            _entries[baseId] = [];
        }
    }

    public string GetBasePath(string baseId)
    {
        lock (_lock)
        {
            return _basePaths.GetValueOrDefault(baseId, "");
        }
    }

    /// <summary>Get the entry solely from the cache (you probably want ResolveEntry instead).</summary>
    public MetaEntry? GetEntry(string baseId, string resourcePath)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(baseId, out var map))
            {
                return null;
            }

            return map.GetValueOrDefault(resourcePath);
        }
    }

    /// <summary>Get the entry from cache and verify that it isn't stale (within reason).</summary>
    public MetaEntry ResolveEntry(string baseId, string resourcePath, string expectedEtag = "")
    {
        resourcePath = FileNaming.RemoveInvalidPathChars(resourcePath);
        var entry = GetEntry(baseId, resourcePath);
        if (entry is null)
        {
            return StaleEntry(baseId, resourcePath);
        }

        var basePath = GetBasePath(baseId);
        var realPath = Path.Combine(basePath, resourcePath);

        // is the file really there? if not -> stale
        if (!File.Exists(realPath))
        {
            Disown(baseId, resourcePath);
            return StaleEntry(baseId, resourcePath);
        }

        // C++ also requires the file to be readable (finfo.isReadable()): a cached file
        // that exists but cannot be opened (chmod 000, foreign owner) is degraded to a
        // stale entry AND disowned, so the download task rebuilds it instead of failing.
        // The probe costs one open/close — the same order as C++'s per-resolve
        // QFileInfo::isReadable (stat+access).
        try
        {
            using var probe = File.OpenRead(realPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Disown(baseId, resourcePath);
            return StaleEntry(baseId, resourcePath);
        }

        if (expectedEtag.Length > 0 && expectedEtag != entry.Etag)
        {
            Disown(baseId, resourcePath);
            return StaleEntry(baseId, resourcePath);
        }

        // if the file changed, check md5sum
        var fileLastChanged = new DateTimeOffset(File.GetLastWriteTimeUtc(realPath)).ToUnixTimeMilliseconds();
        if (fileLastChanged != entry.LocalChangedTimestamp)
        {
            string md5Sum;
            try
            {
                using var stream = File.OpenRead(realPath);
                md5Sum = Convert.ToHexStringLower(MD5.HashData(stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                // no disown here (C++ keeps the entry when the md5 open fails after the
                // stat succeeded — a TOCTOU race), so a later resolve can still recover.
                return StaleEntry(baseId, resourcePath);
            }

            if (entry.Md5Sum != md5Sum)
            {
                Disown(baseId, resourcePath);
                return StaleEntry(baseId, resourcePath);
            }

            // md5sums matched... keep entry and save the new state to file
            entry.LocalChangedTimestamp = fileLastChanged;
            SaveEventually();
        }

        // Get rid of old entries, to prevent cache problems
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (entry.IsExpired(currentTime - fileLastChanged / 1000))
        {
            Disown(baseId, resourcePath);
            return StaleEntry(baseId, resourcePath);
        }

        // entry passed all the checks we cared about. (the base path is not cached on the
        // entry — E4: GetFullPath resolves through the mount table, so a remount of the
        // base re-points the entry without a rewrite)
        return entry;
    }

    public bool UpdateEntry(MetaEntry staleEntry)
    {
        if (!_basePaths.ContainsKey(staleEntry.BaseId))
        {
            return false;
        }

        if (staleEntry.IsStale)
        {
            return false;
        }

        lock (_lock)
        {
            _entries[staleEntry.BaseId][staleEntry.RelativePath] = staleEntry;
        }

        SaveEventually();
        return true;
    }

    public bool EvictEntry(MetaEntry? entry)
    {
        if (entry is null)
        {
            return false;
        }

        entry.IsStale = true;
        SaveEventually();
        return true;
    }

    public bool EvictAll()
    {
        var ret = true;
        lock (_lock)
        {
            foreach (var (baseId, map) in _entries)
            {
                foreach (var entry in map.Values)
                {
                    entry.IsStale = true;
                }

                map.Clear();
                var basePath = _basePaths.GetValueOrDefault(baseId, "");
                if (basePath.Length > 0 && Directory.Exists(basePath))
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(basePath))
                        {
                            File.Delete(file);
                        }
                    }
                    catch (IOException)
                    {
                        ret = false;
                    }
                }
            }
        }

        return ret;
    }

    private void Disown(string baseId, string resourcePath)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(baseId, out var map))
            {
                map.Remove(resourcePath);
            }
        }
    }

    private MetaEntry StaleEntry(string baseId, string resourcePath)
    {
        var entry = new MetaEntry
        {
            BaseId = baseId,
            RelativePath = resourcePath,
        };
        entry.SetOwner(this);
        entry.IsStale = true;
        return entry;
    }

    public void Load()
    {
        if (_indexFile is null || !File.Exists(_indexFile))
        {
            return;
        }

        MetaCacheFileJson? root;
        try
        {
            root = JsonSerializer.Deserialize(File.ReadAllText(_indexFile),
                MetaCacheJsonContext.Default.MetaCacheFileJson);
        }
        catch (JsonException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            // C++ Load() silently skips a ledger that exists but cannot be opened
            // (index.open(ReadOnly) fails -> bare return): the metacache is a true cache
            // whose loss only costs re-downloads, so the host keeps starting and the
            // ledger is rebuilt from scratch instead of failing startup.
            return;
        }

        if (root?.Version != "1" || root.Entries is not { } array)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var raw in array)
            {
                var baseId = raw.Base ?? "";
                if (!_basePaths.ContainsKey(baseId))
                {
                    continue;
                }

                var entry = new MetaEntry
                {
                    BaseId = baseId,
                    RelativePath = raw.Path ?? "",
                    Md5Sum = raw.Md5Sum ?? "",
                    Etag = raw.Etag ?? "",
                    LocalChangedTimestamp = raw.LastChangedTimestamp,
                    RemoteChangedTimestamp = raw.RemoteChangedTimestamp ?? "",
                    IsEternal = raw.Eternal ?? false,
                };
                if (!entry.IsEternal)
                {
                    entry.CurrentAge = raw.CurrentAge ?? 0;
                    entry.MaxAge = raw.MaxAge ?? 0;
                }

                // presumed innocent until closer examination
                entry.SetOwner(this);
                entry.IsStale = false;
                _entries[baseId][entry.RelativePath] = entry;
            }
        }
    }

    public void SaveEventually()
    {
        // reset the save timer (30 seconds, like the C++ batching timer)
        lock (_lock)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => SaveNow(), null, 30_000, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        if (_indexFile is null)
        {
            return;
        }

        MetaCacheFileJson entriesRoot;
        lock (_lock)
        {
            var entriesArr = new List<MetaCacheEntryJson>();
            foreach (var map in _entries.Values)
            {
                foreach (var entry in map.Values)
                {
                    // do not save stale entries. they are dead.
                    if (entry.IsStale)
                    {
                        continue;
                    }

                    entriesArr.Add(new MetaCacheEntryJson(
                        Base: entry.BaseId,
                        Path: entry.RelativePath,
                        Md5Sum: entry.Md5Sum,
                        Etag: entry.Etag,
                        LastChangedTimestamp: entry.LocalChangedTimestamp,
                        // absent when empty; the eternal/age pair is one-or-the-other
                        // (WhenWritingNull drops the unused halves, like the former
                        // conditional writes)
                        RemoteChangedTimestamp:
                        entry.RemoteChangedTimestamp.Length > 0 ? entry.RemoteChangedTimestamp : null,
                        Eternal: entry.IsEternal ? true : null,
                        CurrentAge: entry.IsEternal ? null : entry.CurrentAge,
                        MaxAge: entry.IsEternal ? null : entry.MaxAge));
                }
            }

            entriesRoot = new MetaCacheFileJson(Version: "1", Entries: entriesArr);
        }

        try
        {
            var dir = Path.GetDirectoryName(_indexFile);
            if (dir is not null)
            {
                _ = FileOps.EnsureFolderPathExists(new LocalPath(dir));
            }

            // Qt's QJsonDocument writes `"` as `\"` and leaves unicode alone; match that so
            // metacache files stay byte-compatible with PrismLauncher's.
            File.WriteAllText(
                _indexFile,
                JsonSerializer.Serialize(entriesRoot, MetaCacheJson.WriterTypeInfo));
        }
        catch (IOException) { }
    }
}
