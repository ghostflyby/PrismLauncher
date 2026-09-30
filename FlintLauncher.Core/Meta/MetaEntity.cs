// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/BaseEntity.{h,cpp} (BaseEntity, ParsingValidator and
// BaseEntityLoadTask). Deviations from the C++ original:
//  - The shared meta cache is a static HttpMetaCache here; the C++ code reaches it through
//    APPLICATION->metacache() (base "meta" pointing at the CWD-relative "meta" folder, which
//    is preserved). The "MetaURLOverride" setting is not part of the settings layer yet, so
//    Url() always resolves against BuildConfig.META_URL (FlintConfig.MetaUrl).
//  - The NetJob wrapper of BaseEntityLoadTask is reduced to awaiting the single NetRequest
//    directly (NetJob + setAskRetry(false) + one request has no extra semantics); progress,
//    status and details signal propagation from the inner request is not ported.
//  - The QFile/CSRF-era details (partial downloads, etag bookkeeping beyond the cache entry)
//    follow the current upstream implementation: local-cache-first with sha256 verification,
//    checksummed re-download, and a ParsingValidator so unparseable bodies are never
//    committed to the cache (the entry goes stale on failure).
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Meta;

public enum LoadStatus
{
    NotLoaded,
    Local,
    Remote,
}

public abstract class MetaEntity
{
    private LoadStatus _loadStatus = LoadStatus.NotLoaded;

    private FlintTask? _task;

    /// <summary>The shared "meta" cache base (C++: APPLICATION->metacache(), base "meta").
    /// Injection migration: the per-runtime cache is assigned via <see cref="SetMetaCache"/>
    /// (propagated down the Index/VersionList/MetaVersion chain at entity adoption); the
    /// static default remains only for bare test fixtures until the test phase removes it.</summary>
    public static readonly HttpMetaCache SharedCache = CreateSharedCache();

    /// <summary>The cache this entity downloads through and resolves its local file under
    /// (base "meta").</summary>
    public HttpMetaCache Cache { get; private set; } = SharedCache;

    /// <summary>Wire this entity (and its future children, via the adoption points) to a
    /// runtime-owned cache.</summary>
    public void SetMetaCache(HttpMetaCache cache) => Cache = cache;

    /// <summary>Absolute path of a local metadata file under the "meta" base.</summary>
    protected string MetaPath(string localFile)
    {
        var baseDir = Cache.GetBasePath("meta");
        return baseDir.Length != 0 ? FileSystem.PathCombine(baseDir, localFile) : FileSystem.PathCombine("meta", localFile);
    }

    private static HttpMetaCache CreateSharedCache()
    {
        var cache = new HttpMetaCache();
        // the C++ Application registers the "meta" base pointing at <cwd>/meta, matching the
        // QDir("meta").absoluteFilePath(...) used for the direct local-file reads below
        cache.AddBase("meta", Path.GetFullPath("meta"));
        return cache;
    }

    /// <summary>the expected sha256</summary>
    protected string Sha256 = "";

    /// <summary>the file sha256</summary>
    protected string FileSha256 = "";

    public abstract string LocalFilename();

    public virtual Uri Url()
    {
        return new Uri(new Uri(FlintConfig.Default.MetaUrl), LocalFilename());
    }

    public bool IsLoaded()
    {
        // consider it loaded only if the main hash is either empty and was remote loaded or the
        // hashes match and was loaded
        return Sha256.Length == 0
            ? _loadStatus == LoadStatus.Remote
            : _loadStatus != LoadStatus.NotLoaded && Sha256 == FileSha256;
    }

    public LoadStatus Status() => _loadStatus;

    /// <summary>for parsers</summary>
    public void SetSha256(string sha256) => Sha256 = sha256;

    public abstract void Parse(JsonObject obj);

    /// <summary>Port of BaseEntity::loadTask().</summary>
    public FlintTask LoadTask(NetMode loadType = NetMode.Online, bool forceReload = false)
    {
        if (_task is not null && _task.IsRunning)
        {
            return _task;
        }
        _task = new MetaEntityLoadTask(this, loadType, forceReload);
        return _task;
    }

    /// <summary>
    /// Port of Meta::ParsingValidator: buffers the response body, parses it into the entity and
    /// fails the download when the body is not valid metadata (so it never reaches the cache).
    /// </summary>
    private sealed class ParsingValidator : Validator
    {
        private readonly MetaEntity _entity;
        private readonly List<byte> _data = [];

        public ParsingValidator(MetaEntity entity)
        {
            _entity = entity;
        }

        public override bool Init(NetRequestSpec request)
        {
            _data.Clear();
            return true;
        }

        public override bool Write(byte[] data)
        {
            _data.AddRange(data);
            return true;
        }

        public override bool Abort()
        {
            _data.Clear();
            return true;
        }

        public override bool Validate(NetResponseInfo response)
        {
            var fname = _entity.LocalFilename();
            try
            {
                using var doc = JsonUtils.RequireDocument(
                    System.Text.Encoding.UTF8.GetString([.. _data]), fname);
                var obj = JsonUtils.RequireObject(doc, fname).ToJsonObject();
                _entity.Parse(obj);
                return true;
            }
            catch (Exception e) when (e is JsonParseException or FormatException)
            {
                // qWarning() << "Unable to parse response:" << e.cause();
                return false;
            }
        }
    }

    /// <summary>Port of Meta::BaseEntityLoadTask.</summary>
    private sealed class MetaEntityLoadTask : FlintTask
    {
        private readonly MetaEntity _entity;
        private readonly NetMode _mode;
        private readonly bool _forceReload;

        public MetaEntityLoadTask(MetaEntity entity, NetMode mode, bool forceReload)
        {
            _entity = entity;
            _mode = mode;
            _forceReload = forceReload;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var fname = _entity.MetaPath(_entity.LocalFilename());
            var hashMatches = false;
            // the file exists on disk try to load it
            if (File.Exists(fname))
            {
                var fileData = Array.Empty<byte>();
                try
                {
                    // read local file if nothing is loaded yet
                    if (_entity._loadStatus == LoadStatus.NotLoaded || _entity.FileSha256.Length == 0)
                    {
                        EmitStatus("Loading local file");
                        fileData = FileSystem.Read(fname);
                        _entity.FileSha256 = Convert.ToHexStringLower(SHA256.HashData(fileData));
                    }

                    // on online the hash needs to match
                    var expected = _entity.Sha256;
                    var actual = _entity.FileSha256;
                    hashMatches = expected == actual;
                    if (_mode == NetMode.Online && _entity.Sha256.Length != 0 && !hashMatches)
                    {
                        throw new JsonParseException(
                            $"Checksum mismatch, expected sha256: {expected}, got: {actual}");
                    }

                    // load local file
                    if (_entity._loadStatus == LoadStatus.NotLoaded)
                    {
                        using var doc = JsonUtils.RequireDocument(
                            System.Text.Encoding.UTF8.GetString(fileData), fname);
                        var obj = JsonUtils.RequireObject(doc, fname).ToJsonObject();
                        _entity.Parse(obj);
                        _entity._loadStatus = LoadStatus.Local;
                    }
                }
                catch (Exception)
                {
                    // C++ catches its ::Exception root here (parse errors, checksum mismatch and
                    // read failures all derive from it)
                    // qCritical() << "Unable to parse file ...";
                    // just make sure it's gone and we never consider it again.
                    FileSystem.DeletePath(fname);
                    _entity._loadStatus = LoadStatus.NotLoaded;
                }
            }
            // if we need remote update, run the update task
            var wasLoadedOffline = _entity._loadStatus != LoadStatus.NotLoaded && _mode == NetMode.Offline;
            // if hash is not present always fetch from remote (e.g. the main index file), else
            // only fetch if the hash doesn't match
            var wasLoadedRemote = _entity.Sha256.Length == 0
                ? _entity._loadStatus == LoadStatus.Remote
                : hashMatches;
            if (wasLoadedOffline || (wasLoadedRemote && !_forceReload))
            {
                EmitSucceeded();
                return;
            }

            var entry = _entity.Cache.ResolveEntry("meta", _entity.LocalFilename());
            if (_forceReload)
            {
                // clear validators so manual refreshes fetch a fresh body
                entry.Etag = "";
                entry.RemoteChangedTimestamp = "";
            }
            entry.SetStale(true);
            // Net::ApiRequest::makeCached == makeCached + AddAPIHeaders
            var dl = NetRequest.MakeCached(_entity.Cache, _entity.Url(), entry, RequestOptions.AddAPIHeaders);
            /*
             * The validator parses the file and loads it into the object.
             * If that fails, the file is not written to storage.
             */
            if (_entity.Sha256.Length != 0)
            {
                dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.SHA256, _entity.Sha256));
            }
            dl.AddValidator(new ParsingValidator(_entity));

            await dl.StartAsync(cancellationToken).ConfigureAwait(false);
            if (dl.State == TaskState.Succeeded)
            {
                _entity._loadStatus = LoadStatus.Remote;
                _entity.FileSha256 = _entity.Sha256;
                EmitSucceeded();
            }
            else
            {
                EmitFailed(dl.FailReason);
            }
        }
    }
}
