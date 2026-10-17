// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/HttpMetaCache.{h,cpp}: on-disk download cache index. The index file
// format is kept byte-compatible with PrismLauncher's "metacache" JSON (version "1").

using System.Security;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.Net;

public sealed class MetaEntry
{
    private bool stale = true;

    /// <summary>The cache whose mount table resolves <see cref="GetFullPath"/> (E4: the
    /// base path is NOT a field on the entry — the storage layer owns the baseId → root
    /// table and the key → path resolution; the disk format never carried it anyway).</summary>
    private HttpMetaCache? owner;

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

    public bool IsStale => stale;

    public void SetStale(bool stale) => this.stale = stale;

    /// <summary>Full path of the cached file: resolved LIVE through the owning cache's
    /// mount table (E4 — remounting a base re-points every entry with it). Fails fast when
    /// the entry has no owning cache or its base is not registered — the silent fallback
    /// to a process-CWD-relative path is exactly the E6 defect shape (② lazy bases → CWD
    /// landing) and must not come back through a side door.</summary>
    public string GetFullPath()
    {
        var owner = this.owner ?? throw new InvalidOperationException(
            "meta entry has no owning cache — resolve it through HttpMetaCache.ResolveEntry");
        var basePath = owner.GetBasePath(BaseId);
        return basePath.Length != 0
            ? FileSystem.PathCombine(basePath, RelativePath)
            : throw new InvalidOperationException(
                $"base '{BaseId}' is not registered on the owning meta cache — the composition root must register it");
    }

    public bool IsExpired(long offset) => !IsEternal && CurrentAge >= MaxAge - offset;

    internal void SetOwner(HttpMetaCache? owner) => this.owner = owner;
}

public sealed class HttpMetaCache : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, MetaEntry>> _entries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _basePaths = new(StringComparer.Ordinal);
    private readonly string? _indexFile;
    private Timer? saveTimer;

    public HttpMetaCache(string? path = null)
    {
        _indexFile = string.IsNullOrEmpty(path) ? null : path;
    }

    public void Dispose()
    {
        saveTimer?.Dispose();
        saveTimer = null;
        SaveNow();
    }

    public void AddBase(string baseId, string basePath)
    {
        lock (_lock)
        {
            if (_basePaths.ContainsKey(baseId))
            {
                return;
            }
            _basePaths[baseId] = basePath;
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
        resourcePath = FileSystem.RemoveInvalidPathChars(resourcePath);
        var entry = GetEntry(baseId, resourcePath);
        if (entry is null)
        {
            return StaleEntry(baseId, resourcePath);
        }

        var basePath = GetBasePath(baseId);
        var realPath = FileSystem.PathCombine(basePath, resourcePath);

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
        entry.SetStale(true);
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
                    entry.SetStale(true);
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
        entry.SetStale(true);
        return entry;
    }

    public void Load()
    {
        if (_indexFile is null || !File.Exists(_indexFile))
        {
            return;
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(_indexFile));
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
        if (root is not JsonObject rootObj)
        {
            return;
        }
        if (GetStr(rootObj, "version") != "1")
        {
            return;
        }
        if (rootObj["entries"] is not JsonArray array)
        {
            return;
        }
        lock (_lock)
        {
            foreach (var elementObj in array)
            {
                if (elementObj is not JsonObject entryObj)
                {
                    continue;
                }
                var baseId = GetStr(entryObj, "base");
                if (!_basePaths.ContainsKey(baseId))
                {
                    continue;
                }
                var entry = new MetaEntry
                {
                    BaseId = baseId,
                    RelativePath = GetStr(entryObj, "path"),
                    Md5Sum = GetStr(entryObj, "md5sum"),
                    Etag = GetStr(entryObj, "etag"),
                    LocalChangedTimestamp = (long)GetDouble(entryObj, "last_changed_timestamp"),
                    RemoteChangedTimestamp = GetStr(entryObj, "remote_changed_timestamp"),
                    IsEternal = GetBool(entryObj, "eternal"),
                };
                if (!entry.IsEternal)
                {
                    entry.CurrentAge = (long)GetDouble(entryObj, "current_age");
                    entry.MaxAge = (long)GetDouble(entryObj, "max_age");
                }
                // presumed innocent until closer examination
                entry.SetOwner(this);
                entry.SetStale(false);
                _entries[baseId][entry.RelativePath] = entry;
            }
        }
    }

    private static string GetStr(JsonObject obj, string key, string def = "") =>
        JsonUtils.EnsureString(obj[key], def);

    private static bool GetBool(JsonObject obj, string key, bool def = false) =>
        JsonUtils.EnsureBoolean(obj[key], def);

    private static double GetDouble(JsonObject obj, string key, double def = 0) =>
        JsonUtils.EnsureDouble(obj[key], def);

    public void SaveEventually()
    {
        // reset the save timer (30 seconds, like the C++ batching timer)
        lock (_lock)
        {
            saveTimer?.Dispose();
            saveTimer = new Timer(_ => SaveNow(), null, 30_000, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        if (_indexFile is null)
        {
            return;
        }
        JsonObject entriesRoot;
        lock (_lock)
        {
            entriesRoot = new JsonObject
            {
                ["version"] = "1",
            };
            var entriesArr = new JsonArray();
            foreach (var map in _entries.Values)
            {
                foreach (var entry in map.Values)
                {
                    // do not save stale entries. they are dead.
                    if (entry.IsStale)
                    {
                        continue;
                    }
                    var entryObj = new JsonObject
                    {
                        ["base"] = entry.BaseId,
                        ["path"] = entry.RelativePath,
                        ["md5sum"] = entry.Md5Sum,
                        ["etag"] = entry.Etag,
                        ["last_changed_timestamp"] = entry.LocalChangedTimestamp,
                    };
                    if (entry.RemoteChangedTimestamp.Length > 0)
                    {
                        entryObj["remote_changed_timestamp"] = entry.RemoteChangedTimestamp;
                    }
                    if (entry.IsEternal)
                    {
                        entryObj["eternal"] = true;
                    }
                    else
                    {
                        entryObj["current_age"] = entry.CurrentAge;
                        entryObj["max_age"] = entry.MaxAge;
                    }
                    entriesArr.Add((JsonNode)entryObj);
                }
            }
            entriesRoot["entries"] = entriesArr;
        }

        try
        {
            var dir = Path.GetDirectoryName(_indexFile);
            if (dir is not null)
            {
                _ = FileSystem.EnsureFolderPathExists(dir);
            }
            // Qt's QJsonDocument writes `"` as `\"` and leaves unicode alone; match that so
            // metacache files stay byte-compatible with PrismLauncher's.
            File.WriteAllText(
                _indexFile,
                entriesRoot.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (IOException)
        {
        }
    }
}
