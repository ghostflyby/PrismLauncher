// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/FileSink.h/.cpp and net/MetaCacheSink.h/.cpp.

using System.Globalization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Net;

/// <summary>Writes the response body to a file via an atomic temp file + rename (PSaveFile).</summary>
public class FileSink(string filename) : Sink
{
    private bool wroteAnyData;

    /// <summary>Writable by MetaCacheSink (C++: protected member access).</summary>
    private protected bool WroteAnyData => wroteAnyData;

    private string? tempFile;

    public string Filename => filename;

    public override bool Init(NetRequestSpec request)
    {
        var result = InitCache(request);
        if (!result)
        {
            return false;
        }

        if (!FileSystem.EnsureFilePathExists(filename))
        {
            FailReason = "Could not create folder for " + filename;
            return false;
        }

        wroteAnyData = false;
        tempFile = filename + ".flint-tmp";
        try
        {
            File.Create(tempFile).Dispose();
        }
        catch (IOException e)
        {
            FailReason = $"Could not open {filename} for writing: {e.Message}";
            tempFile = null;
            return false;
        }

        if (InitAllValidators(request))
        {
            return true;
        }

        FailReason = "Failed to initialize validators";
        return false;
    }

    public override bool Write(byte[] data)
    {
        if (tempFile is null)
        {
            FailReason = "Writing into a closed file sink";
            return false;
        }

        try
        {
            using var stream = File.Open(tempFile, FileMode.Append, FileAccess.Write);
            stream.Write(data);
        }
        catch (IOException e)
        {
            FailReason = $"Failed writing into {filename}: {e.Message}";
            CancelWriting();
            wroteAnyData = false;
            return false;
        }

        if (!WriteAllValidators(data))
        {
            FailReason = $"Failed writing into {filename}: validators failed";
            CancelWriting();
            wroteAnyData = false;
            return false;
        }

        wroteAnyData = true;
        return true;
    }

    public override bool Abort()
    {
        CancelWriting();
        FailAllValidators();
        return false;
    }

    public override bool Finalize(NetResponseInfo response)
    {
        // this leaves out 304 Not Modified
        var gotFile = response.StatusCode is 200 or 203;

        // if we wrote any data to the save file, we try to commit the data to the real file.
        // if it actually got a proper file, we write it even if it was empty
        if (gotFile || wroteAnyData)
        {
            // ask validators for data consistency: only for actual downloads, not cache hits
            if (!FinalizeAllValidators(response))
            {
                FailReason = "Failed to finalize validators";
                return false;
            }

            if (tempFile is not null)
            {
                try
                {
                    File.Move(tempFile, filename, overwrite: true);
                }
                catch (IOException e)
                {
                    FailReason = $"Failed to commit changes to {filename}: {e.Message}";
                    CancelWriting();
                    return false;
                }

                tempFile = null;
            }
        }

        CancelWriting();
        return FinalizeCache(response);
    }

    public override bool HasLocalData()
    {
        var info = new FileInfo(filename);
        return info.Exists && info.Length != 0;
    }

    private void CancelWriting()
    {
        if (tempFile is not null)
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException) { }

            tempFile = null;
        }

        wroteAnyData = false;
    }

    protected virtual bool InitCache(NetRequestSpec request) => true;

    protected virtual bool FinalizeCache(NetResponseInfo response) => true;
}

/// <summary>
/// Port of net/MetaCacheSink: writes into the HttpMetaCache entry, performs If-None-Match
/// revalidation and records the new etag/md5 on the entry.
/// </summary>
public sealed class MetaCacheSink(MetaEntry entry, ChecksumValidator md5Sum, bool isEternal = false) : FileSink(entry.GetFullPath())
{
    /// <summary>The owning cache; when set, the entry is re-registered after finalize
    /// (C++: APPLICATION->metacache()->updateEntry).</summary>
    public HttpMetaCache? MetaCache { get; init; }

    public override bool HasLocalData()
    {
        var info = new FileInfo(entry.GetFullPath());
        return info.Exists && info.Length != 0;
    }

    protected override bool InitCache(NetRequestSpec request)
    {
        if (entry.IsStale)
        {
            return true;
        }

        if (!HasLocalData() && entry.Md5Sum.Length == 0)
        {
            return true;
        }

        // set the etag to revalidate against
        if (entry.Etag.Length > 0)
        {
            request.Headers["If-None-Match"] = entry.Etag;
        }

        return true;
    }

    protected override bool FinalizeCache(NetResponseInfo response)
    {
        // Port of MetaCacheSink::finalizeCache (full cache-lifetime semantics): md5 only when
        // data was written, etag/last-modified/max-age/Age bookkeeping, always succeeds.
        if (WroteAnyData)
        {
            var hash = md5Sum.Hash();
            if (hash.Length > 0)
            {
                entry.Md5Sum = Convert.ToHexStringLower(hash);
            }
        }

        entry.Etag = response.Headers.GetValueOrDefault("ETag", "");

        if (response.Headers.TryGetValue("Last-Modified", out var lastModified))
        {
            entry.RemoteChangedTimestamp = lastModified;
        }

        var fullPath = entry.GetFullPath();
        entry.LocalChangedTimestamp = File.Exists(fullPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeMilliseconds()
            : 0;

        if (isEternal)
        {
            entry.IsEternal = true;
        }
        else if (response.Headers.TryGetValue("Cache-Control", out var cacheControl))
        {
            var match = Regex.Match(cacheControl, "max-age=([0-9]+)");
            if (match.Success)
            {
                entry.MaxAge = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        else if (response.Headers.TryGetValue("Expires", out var expiresHeader))
        {
            if (DateTimeOffset.TryParse(expiresHeader, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var expires))
            {
                entry.MaxAge = expires.ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }
        else
        {
            // MetaCacheSink.cpp: #define MAX_TIME_TO_EXPIRE 1 * 7 * 24 * 60 * 60
            entry.MaxAge = 7 * 24 * 60 * 60;
        }

        entry.CurrentAge = response.Headers.TryGetValue("Age", out var ageHeader)
                            && long.TryParse(ageHeader, out var age)
            ? age
            : 0;

        entry.SetStale(false);
        MetaCache?.UpdateEntry(entry);
        return true;
    }
}
