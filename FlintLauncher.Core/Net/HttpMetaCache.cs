// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/HttpMetaCache.{h,cpp}: on-disk download cache index. The index file
// format is kept byte-compatible with PrismLauncher's "metacache" JSON (version "1").
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.Net;

public sealed class MetaEntry
{
    private bool _stale = true;

    public string BaseId { get; set; } = "";

    public string BasePath { get; set; } = "";

    public string RelativePath { get; set; } = "";

    public string Md5Sum { get; set; } = "";

    public string Etag { get; set; } = "";

    public long LocalChangedTimestamp { get; set; }

    /// <summary>RFC 2822 encoded time, as sent by the remote.</summary>
    public string RemoteChangedTimestamp { get; set; } = "";

    public long CurrentAge { get; set; }

    public long MaxAge { get; set; }

    public bool IsEternal { get; set; }

    public bool IsStale => _stale;

    public void SetStale(bool stale) => _stale = stale;

    public string GetFullPath() => FileSystem.PathCombine(BasePath, RelativePath);

    public bool IsExpired(long offset) => !IsEternal && CurrentAge >= MaxAge - offset;
}

public sealed class HttpMetaCache : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, MetaEntry>> _entries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _basePaths = new(StringComparer.Ordinal);
    private readonly string? _indexFile;
    private System.Threading.Timer? _saveTimer;

    public HttpMetaCache(string? path = null)
    {
        _indexFile = string.IsNullOrEmpty(path) ? null : path;
    }

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
                md5Sum = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream)).ToLowerInvariant();
            }
            catch (IOException)
            {
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

        // entry passed all the checks we cared about.
        entry.BasePath = basePath;
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
            BasePath = GetBasePath(baseId),
            RelativePath = resourcePath,
        };
        entry.SetStale(true);
        return entry;
    }

    public void Load()
    {
        if (_indexFile is null || !File.Exists(_indexFile))
        {
            return;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(_indexFile));
        }
        catch (JsonException)
        {
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            if (GetStr(root, "version") != "1")
            {
                return;
            }
            if (!root.TryGetProperty("entries", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            lock (_lock)
            {
                foreach (var elementObj in array.EnumerateArray())
                {
                    if (elementObj.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var baseId = GetStr(elementObj, "base");
                    if (!_basePaths.ContainsKey(baseId))
                    {
                        continue;
                    }
                    var entry = new MetaEntry
                    {
                        BaseId = baseId,
                        RelativePath = GetStr(elementObj, "path"),
                        Md5Sum = GetStr(elementObj, "md5sum"),
                        Etag = GetStr(elementObj, "etag"),
                        LocalChangedTimestamp = (long)GetDouble(elementObj, "last_changed_timestamp"),
                        RemoteChangedTimestamp = GetStr(elementObj, "remote_changed_timestamp"),
                        IsEternal = GetBool(elementObj, "eternal"),
                    };
                    if (!entry.IsEternal)
                    {
                        entry.CurrentAge = (long)GetDouble(elementObj, "current_age");
                        entry.MaxAge = (long)GetDouble(elementObj, "max_age");
                    }
                    // presumed innocent until closer examination
                    entry.SetStale(false);
                    _entries[baseId][entry.RelativePath] = entry;
                }
            }
        }
    }

    private static string GetStr(JsonElement obj, string key, string def = "") =>
        obj.TryGetProperty(key, out var v) ? JsonUtils.EnsureString(v, def) : def;

    private static bool GetBool(JsonElement obj, string key, bool def = false) =>
        obj.TryGetProperty(key, out var v) ? JsonUtils.EnsureBoolean(v, def) : def;

    private static double GetDouble(JsonElement obj, string key, double def = 0) =>
        obj.TryGetProperty(key, out var v) ? JsonUtils.EnsureDouble(v, def) : def;

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
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (IOException)
        {
        }
    }
}
