// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/BaseEntity.{h,cpp} (BaseEntity, ParsingValidator and
// BaseEntityLoadTask). Deviations from the C++ original:
//  - The C++ code reaches the shared cache through APPLICATION->metacache() (base "meta");
//    the port stamps that cache onto the entity chain at adoption (SetMetaCache — the
//    composition root's HttpMetaCache, base "meta" under the cache root, E3/E6). There is
//    NO static fallback: a load without a stamp fails fast. The "MetaURLOverride" setting
//    is not part of the settings layer yet, so Url() always resolves against
//    BuildConfig.META_URL (FlintConfig.MetaUrl).
//  - The NetJob wrapper of BaseEntityLoadTask is reduced to awaiting the single NetRequest
//    directly (NetJob + setAskRetry(false) + one request has no extra semantics); progress,
//    status and details signal propagation from the inner request is not ported.
//  - The QFile/CSRF-era details (partial downloads, etag bookkeeping beyond the cache entry)
//    follow the current upstream implementation: local-cache-first with sha256 verification,
//    checksummed re-download, and a ParsingValidator so unparseable bodies are never
//    committed to the cache (the entry goes stale on failure).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
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
    private LoadStatus loadStatus = LoadStatus.NotLoaded;

    private FlintTask? task;

    /// <summary>The cache this entity downloads through and resolves its local file under
    /// (base "meta"; C++: APPLICATION->metacache()). E6: the static CWD-anchored fallback is
    /// GONE — the composition root's cache is stamped down the Index/VersionList/entity
    /// chain at adoption (SetMetaCache), so a load without a stamp fails fast naming the
    /// missing wire instead of silently re-downloading into the process working directory.</summary>
    public HttpMetaCache? Cache { get; set; }

    /// <summary>The stamped runtime cache (naming the missing wire when unset — the E6
    /// bare-fixture fallback must not come back).</summary>
    protected HttpMetaCache RequireCache() => Cache ?? throw new InvalidOperationException(
        "no meta cache stamped on this entity — wire the runtime cache (SetMetaCache)");

    /// <summary>Absolute path of a local metadata file under the "meta" base.</summary>
    protected string MetaPath(string localFile) =>
        FileSystem.PathCombine(RequireCache().GetBasePath("meta"), localFile);

    /// <summary>the expected sha256</summary>
    public string Sha256 = "";

    /// <summary>the file sha256</summary>
    protected string FileSha256 = "";

    public abstract string LocalFilename { get; }

    public virtual Uri Url()
    {
        return new Uri(new Uri(FlintConfig.Default.MetaUrl), LocalFilename);
    }

    public virtual bool IsLoaded =>
        // consider it loaded only if the main hash is either empty and was remote loaded or the
        // hashes match and was loaded
        Sha256.Length == 0
            ? loadStatus == LoadStatus.Remote
            : loadStatus != LoadStatus.NotLoaded && Sha256 == FileSha256;

    public LoadStatus Status() => loadStatus;

    /// <summary>for parsers</summary>
    public abstract void Parse(JsonObject obj);

    /// <summary>Port of BaseEntity::loadTask().</summary>
    public FlintTask LoadTask(NetMode loadType = NetMode.Online, bool forceReload = false)
    {
        if (task is not null && task.IsRunning)
        {
            return task;
        }

        task = new MetaEntityLoadTask(this, loadType, forceReload);
        return task;
    }

    /// <summary>
    /// Port of Meta::ParsingValidator: buffers the response body, parses it into the entity and
    /// fails the download when the body is not valid metadata (so it never reaches the cache).
    /// </summary>
    private sealed class ParsingValidator(MetaEntity entity) : Validator
    {
        private readonly List<byte> _data = [];

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
            var fname = entity.LocalFilename;
            try
            {
                var obj = JsonUtils.RequireObject(
                    JsonUtils.RequireNode(Encoding.UTF8.GetString([.. _data]), fname), fname);
                entity.Parse(obj);
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
    private sealed class MetaEntityLoadTask(MetaEntity entity, NetMode mode, bool forceReload) : FlintTask
    {
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var fname = entity.MetaPath(entity.LocalFilename);
            var hashMatches = false;
            // the file exists on disk try to load it
            if (File.Exists(fname))
            {
                var fileData = Array.Empty<byte>();
                try
                {
                    // read local file if nothing is loaded yet
                    if (entity.loadStatus == LoadStatus.NotLoaded || entity.FileSha256.Length == 0)
                    {
                        EmitStatus("Loading local file");
                        fileData = FileSystem.Read(fname);
                        entity.FileSha256 = Convert.ToHexStringLower(SHA256.HashData(fileData));
                    }

                    // on online the hash needs to match
                    var expected = entity.Sha256;
                    var actual = entity.FileSha256;
                    hashMatches = expected == actual;
                    if (mode == NetMode.Online && entity.Sha256.Length != 0 && !hashMatches)
                    {
                        throw new JsonParseException(
                            $"Checksum mismatch, expected sha256: {expected}, got: {actual}");
                    }

                    // load local file
                    if (entity.loadStatus == LoadStatus.NotLoaded)
                    {
                        var obj = JsonUtils.RequireObject(
                            JsonUtils.RequireNode(Encoding.UTF8.GetString(fileData), fname), fname);
                        entity.Parse(obj);
                        entity.loadStatus = LoadStatus.Local;
                    }
                }
                catch (Exception)
                {
                    // C++ catches its ::Exception root here (parse errors, checksum mismatch and
                    // read failures all derive from it)
                    // qCritical() << "Unable to parse file ...";
                    // just make sure it's gone and we never consider it again.
                    FileSystem.DeletePath(fname);
                    entity.loadStatus = LoadStatus.NotLoaded;
                }
            }

            // if we need remote update, run the update task
            var wasLoadedOffline = entity.loadStatus != LoadStatus.NotLoaded && mode == NetMode.Offline;
            // if hash is not present always fetch from remote (e.g. the main index file), else
            // only fetch if the hash doesn't match
            var wasLoadedRemote = entity.Sha256.Length == 0
                ? entity.loadStatus == LoadStatus.Remote
                : hashMatches;
            if (wasLoadedOffline || (wasLoadedRemote && !forceReload))
            {
                EmitSucceeded();
                return;
            }

            var cache = entity.RequireCache();
            var entry = cache.ResolveEntry("meta", entity.LocalFilename);
            if (forceReload)
            {
                // clear validators so manual refreshes fetch a fresh body
                entry.Etag = "";
                entry.RemoteChangedTimestamp = "";
            }

            entry.SetStale(true);
            // Net::ApiRequest::makeCached == makeCached + AddAPIHeaders
            var dl = NetRequest.MakeCached(cache, entity.Url(), entry, RequestOptions.AddApiHeaders);
            /*
             * The validator parses the file and loads it into the object.
             * If that fails, the file is not written to storage.
             */
            if (entity.Sha256.Length != 0)
            {
                dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.SHA256, entity.Sha256));
            }

            dl.AddValidator(new ParsingValidator(entity));

            await dl.StartAsync(cancellationToken).ConfigureAwait(false);
            if (dl.State == TaskState.Succeeded)
            {
                entity.loadStatus = LoadStatus.Remote;
                entity.FileSha256 = entity.Sha256;
                EmitSucceeded();
            }
            else
            {
                EmitFailed(dl.FailReason);
            }
        }
    }
}
