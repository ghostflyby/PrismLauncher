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
// Port of launcher/net/FileSink.h/.cpp and net/MetaCacheSink.h/.cpp.

using System.Globalization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Net;

/// <summary>Writes the response body to a file via an atomic temp file + rename (PSaveFile).</summary>
public class FileSink(AbsolutePath filename) : Sink
{
    private bool _wroteAnyData;

    /// <summary>Writable by MetaCacheSink (C++: protected member access).</summary>
    private protected bool WroteAnyData => _wroteAnyData;

    private AbsolutePath? _tempFile;

    public AbsolutePath Filename => filename;

    public override bool Init(NetRequestSpec request)
    {
        var result = InitCache(request);
        if (!result)
        {
            return false;
        }

        if (!FileOps.EnsureFilePathExists(new AbsolutePath(filename)))
        {
            FailReason = "Could not create folder for " + filename;
            return false;
        }

        _wroteAnyData = false;
        _tempFile = filename.Parent is { } parent
            ? parent / (filename.FileName + ".flint-tmp")
            : new AbsolutePath(filename.Value + ".flint-tmp");
        try
        {
            _tempFile?.Create().Dispose();
        }
        catch (IOException e)
        {
            FailReason = $"Could not open {filename} for writing: {e.Message}";
            _tempFile = null;
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
        if (_tempFile is not { } tempFile)
        {
            FailReason = "Writing into a closed file sink";
            return false;
        }

        try
        {
            using var stream = tempFile.Open(FileMode.Append, FileAccess.Write);
            stream.Write(data);
        }
        catch (IOException e)
        {
            FailReason = $"Failed writing into {filename}: {e.Message}";
            CancelWriting();
            _wroteAnyData = false;
            return false;
        }

        if (!WriteAllValidators(data))
        {
            FailReason = $"Failed writing into {filename}: validators failed";
            CancelWriting();
            _wroteAnyData = false;
            return false;
        }

        _wroteAnyData = true;
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
        if (gotFile || _wroteAnyData)
        {
            // ask validators for data consistency: only for actual downloads, not cache hits
            if (!FinalizeAllValidators(response))
            {
                FailReason = "Failed to finalize validators";
                return false;
            }

            if (_tempFile is { } tempFile)
            {
                try
                {
                    tempFile.Move(filename, overwrite: true);
                }
                catch (IOException e)
                {
                    FailReason = $"Failed to commit changes to {filename}: {e.Message}";
                    CancelWriting();
                    return false;
                }

                _tempFile = null;
            }
        }

        CancelWriting();
        return FinalizeCache(response);
    }

    public override bool HasLocalData()
    {
        var info = new FileInfo(filename.Value);
        return info.Exists && info.Length != 0;
    }

    private void CancelWriting()
    {
        if (_tempFile is { } tempFile)
        {
            try
            {
                tempFile.Delete();
            }
            catch (IOException) { }

            _tempFile = null;
        }

        _wroteAnyData = false;
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
        var info = new FileInfo(entry.GetFullPath().Value);
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
        entry.LocalChangedTimestamp = fullPath.ExistsFile()
            ? new DateTimeOffset(fullPath.GetLastWriteTimeUtc()).ToUnixTimeMilliseconds()
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

        entry.IsStale = false;
        MetaCache?.UpdateEntry(entry);
        return true;
    }
}
