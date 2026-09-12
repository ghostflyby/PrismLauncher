// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/FileSink.h/.cpp and net/MetaCacheSink.h/.cpp.
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Net;

/// <summary>Writes the response body to a file via an atomic temp file + rename (PSaveFile).</summary>
public class FileSink : Sink
{
    private readonly string _filename;
    private bool _wroteAnyData;
    private string? _tempFile;

    public FileSink(string filename)
    {
        _filename = filename;
    }

    public string Filename => _filename;

    public override bool Init(NetRequestSpec request)
    {
        var result = InitCache(request);
        if (!result)
        {
            return false;
        }

        if (!FileSystem.EnsureFilePathExists(_filename))
        {
            FailReason = "Could not create folder for " + _filename;
            return false;
        }

        _wroteAnyData = false;
        _tempFile = _filename + ".flint-tmp";
        try
        {
            File.Create(_tempFile).Dispose();
        }
        catch (IOException e)
        {
            FailReason = $"Could not open {_filename} for writing: {e.Message}";
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
        if (_tempFile is null)
        {
            FailReason = "Writing into a closed file sink";
            return false;
        }
        try
        {
            using var stream = File.Open(_tempFile, FileMode.Append, FileAccess.Write);
            stream.Write(data);
        }
        catch (IOException e)
        {
            FailReason = $"Failed writing into {_filename}: {e.Message}";
            CancelWriting();
            _wroteAnyData = false;
            return false;
        }
        if (!WriteAllValidators(data))
        {
            FailReason = $"Failed writing into {_filename}: validators failed";
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
        var gotFile = false;
        // this leaves out 304 Not Modified
        gotFile = response.StatusCode is 200 or 203;

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

            if (_tempFile is not null)
            {
                try
                {
                    File.Move(_tempFile, _filename, overwrite: true);
                }
                catch (IOException e)
                {
                    FailReason = $"Failed to commit changes to {_filename}: {e.Message}";
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
        var info = new FileInfo(_filename);
        return info.Exists && info.Length != 0;
    }

    private void CancelWriting()
    {
        if (_tempFile is not null)
        {
            try
            {
                File.Delete(_tempFile);
            }
            catch (IOException)
            {
            }
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
public sealed class MetaCacheSink : FileSink
{
    private readonly MetaEntry _entry;
    private readonly ChecksumValidator _md5Node;
    private readonly bool _isEternal;

    public MetaCacheSink(MetaEntry entry, ChecksumValidator md5sum, bool isEternal = false)
        : base(entry.GetFullPath())
    {
        _entry = entry;
        _md5Node = md5sum;
        _isEternal = isEternal;
    }

    public override bool HasLocalData()
    {
        var info = new FileInfo(_entry.GetFullPath());
        return info.Exists && info.Length != 0;
    }

    protected override bool InitCache(NetRequestSpec request)
    {
        if (_entry.IsStale)
        {
            return true;
        }
        if (!HasLocalData() && _entry.Md5Sum.Length == 0)
        {
            return true;
        }
        // set the etag to revalidate against
        if (_entry.Etag.Length > 0)
        {
            request.Headers["If-None-Match"] = _entry.Etag;
        }
        return true;
    }

    protected override bool FinalizeCache(NetResponseInfo response)
    {
        if (response.StatusCode is 304 or 200 or 203)
        {
            string hashHex;
            if (_entry.Md5Sum.Length > 0 && response.StatusCode == 304)
            {
                hashHex = _entry.Md5Sum;
            }
            else
            {
                var hash = _md5Node.Hash();
                if (hash.Length == 0)
                {
                    return false;
                }
                hashHex = Convert.ToHexString(hash).ToLowerInvariant();
            }
            _entry.Md5Sum = hashHex;
            _entry.Etag = response.Headers.GetValueOrDefault("ETag", "");
            _entry.SetStale(false);
            _entry.IsEternal = _isEternal;
        }
        return true;
    }
}
