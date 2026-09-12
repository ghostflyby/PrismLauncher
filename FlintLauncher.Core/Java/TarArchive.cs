// SPDX-License-Identifier: GPL-3.0-only
// Minimal ustar/tar.gz reader used by Java::ArchiveDownloadTask. Upstream runs every archive
// through libarchive (MMCZip::ArchiveReader); the .NET port ships this dedicated reader for
// the tar.gz flavour the Java runtime distributions are shipped in (System.IO.Compression only
// covers zip, and no new packages are allowed — hence a hand-rolled ustar parser).
//
// Supported: POSIX ustar headers (name + prefix), GNU long names ('L'/'K'), pax extended
// headers ('x' — path/linkpath/size overrides), regular files, directories, symlinks ('2',
// via File.CreateSymbolicLink) and hard links ('1', best-effort copy). Unknown entry types are
// skipped. Sparse files, multi-volume and base-256 numeric fields are not handled (JDK
// distributions never use them). Like the C++ flow (collectFiles + parse), consumers make two
// passes over the archive: a listing pass to find the first top-level folder and the
// extraction pass.
using System.IO.Compression;
using System.Text;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Java;

public static class TarArchive
{
    private const int BlockSize = 512;

    /// <summary>
    /// Extracts a tar.gz archive into <paramref name="targetDir"/>, skipping entries that do
    /// not live under <paramref name="stripFirstComponent"/> and stripping that prefix (the
    /// ExtractZipTask behaviour). Returns the extracted paths, or null on failure.
    /// </summary>
    public static List<string>? ExtractTarGzFile(string archivePath, string targetDir, string? stripFirstComponent, out string error)
    {
        try
        {
            using var source = File.OpenRead(archivePath);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            return ExtractTarStream(gzip, targetDir, stripFirstComponent, out error);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            error = $"Unable to read tar.gz archive {archivePath}: {e.Message}";
            return null;
        }
    }

    /// <summary>Extracts a plain (uncompressed) tar archive; same semantics as the .tar.gz variant.</summary>
    public static List<string>? ExtractTarFile(string archivePath, string targetDir, string? stripFirstComponent, out string error)
    {
        try
        {
            using var source = File.OpenRead(archivePath);
            return ExtractTarStream(source, targetDir, stripFirstComponent, out error);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            error = $"Unable to read tar archive {archivePath}: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// Lists the entry names of a tar.gz archive (the collectFiles() pass used to determine
    /// which top-level folder to strip). Returns null on failure.
    /// </summary>
    public static List<string>? ListTarGzFile(string archivePath, out string error)
    {
        try
        {
            using var source = File.OpenRead(archivePath);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            return ExtractTarStream(gzip, targetDir: "", stripFirstComponent: null, out error, listOnly: true);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            error = $"Unable to read tar.gz archive {archivePath}: {e.Message}";
            return null;
        }
    }

    /// <summary>Core streaming tar parser.</summary>
    public static List<string>? ExtractTarStream(Stream stream, string targetDir, string? stripFirstComponent, out string error,
                                                 bool listOnly = false)
    {
        error = "";
        var extracted = new List<string>();
        var targetTopDir = listOnly ? "" : Path.GetFullPath(targetDir);
        if (!listOnly)
        {
            FileSystem.EnsureFolderPathExists(targetTopDir);
        }

        var header = new byte[BlockSize];
        // pending overrides carried from the preceding GNU/pax metadata entries
        string? longName = null;
        string? longLink = null;
        PaxOverrides? pax = null;

        while (true)
        {
            if (!ReadBlock(stream, header))
            {
                break; // clean end of stream
            }
            if (IsZeroBlock(header))
            {
                break; // end-of-archive marker (lenient: a single zero block ends the archive)
            }
            if (!ParseHeader(header, out var name, out var mode, out var size, out var typeFlag, out var linkName))
            {
                error = "Failed to parse tar header";
                return null;
            }

            if (typeFlag == 'L')
            {
                longName = ReadSmallPayload(stream, size); // GNU long name for the next entry
                continue;
            }
            if (typeFlag == 'K')
            {
                longLink = ReadSmallPayload(stream, size); // GNU long link target for the next entry
                continue;
            }
            if (typeFlag is 'x' or 'X' or 'g')
            {
                var payload = ReadSmallPayload(stream, size);
                if (typeFlag != 'g')
                {
                    pax = ParsePax(payload, pax);
                }
                continue;
            }

            // apply the accumulated name/link/size overrides
            var effectiveName = longName ?? pax?.Path ?? name;
            var effectiveLink = longLink ?? pax?.LinkPath ?? linkName;
            var effectiveSize = pax?.Size ?? size;
            longName = null;
            longLink = null;
            pax = null;

            if (listOnly)
            {
                extracted.Add(effectiveName);
                SkipData(stream, effectiveSize);
                continue;
            }

            if (stripFirstComponent is not null && !effectiveName.StartsWith(stripFirstComponent, StringComparison.Ordinal))
            {
                SkipData(stream, effectiveSize);
                continue;
            }

            var relativeName = stripFirstComponent is not null ? effectiveName[stripFirstComponent.Length..] : effectiveName;
            relativeName = relativeName.Replace('\\', '/');
            // Fix subdirs/files ending with a / getting transformed into absolute paths
            if (relativeName.StartsWith('/'))
            {
                relativeName = relativeName[1..];
            }
            // Fix weird "folders with a single file get squashed" thing
            string subPath;
            if (relativeName.Contains('/') && !relativeName.EndsWith('/'))
            {
                var sections = relativeName.Split('/');
                subPath = string.Join('/', sections[..^1]) + '/';
                relativeName = sections[^1];
                FileSystem.EnsureFolderPathExists(FileSystem.PathCombine(targetTopDir, subPath));
            }
            else
            {
                subPath = "";
            }

            string targetFilePath;
            if (relativeName.Length == 0)
            {
                targetFilePath = targetTopDir + '/';
            }
            else
            {
                targetFilePath = FileSystem.PathCombine(targetTopDir, subPath, relativeName);
            }

            // zip-slip check: the resolved target must stay inside the target directory
            if (!IsParentOf(targetTopDir, targetFilePath))
            {
                FileSystem.RemoveFiles(extracted);
                error = $"Extracting {relativeName} was cancelled, because it was effectively outside of the target path {targetTopDir}";
                return null;
            }

            if (!ExtractEntry(stream, targetFilePath, typeFlag, effectiveSize, effectiveLink, mode, extracted, out error))
            {
                FileSystem.RemoveFiles(extracted);
                return null;
            }
        }
        return extracted;
    }

    private static bool ExtractEntry(Stream stream, string targetFilePath, char typeFlag, long size, string linkTarget, int mode,
                                     List<string> extracted, out string error)
    {
        error = "";
        try
        {
            switch (typeFlag)
            {
                case '5': // directory
                {
                    var dir = targetFilePath.TrimEnd('/');
                    if (!FileSystem.EnsureFolderPathExists(dir))
                    {
                        error = $"Failed to create directory {dir}";
                        return false;
                    }
                    extracted.Add(targetFilePath);
                    return true;
                }
                case '0' or '\0' or '7': // regular file (the data payload follows)
                {
                    FileSystem.EnsureFilePathExists(targetFilePath);
                    using (var output = File.Create(targetFilePath))
                    {
                        CopyExactly(stream, output, size);
                    }
                    SkipPadding(stream, size);
                    // 0b001001001 == 0o111: any of user/group/other execute
                    if (!OperatingSystem.IsWindows() && (mode & 0b001001001) != 0)
                    {
                        File.SetUnixFileMode(targetFilePath,
                            File.GetUnixFileMode(targetFilePath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherExecute);
                    }
                    extracted.Add(targetFilePath);
                    return true;
                }
                case '2': // symbolic link ("this is *nix only !" upstream)
                {
                    SkipData(stream, size);
                    try
                    {
                        if (File.Exists(targetFilePath) || Directory.Exists(targetFilePath))
                        {
                            // create_link upstream fails on existing targets; skip instead
                            return true;
                        }
                        FileSystem.EnsureFilePathExists(targetFilePath);
                        File.CreateSymbolicLink(targetFilePath, linkTarget);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // best effort like the manifest link handling; never fatal
                    }
                    extracted.Add(targetFilePath);
                    return true;
                }
                case '1': // hard link — best-effort copy from the already-extracted target
                {
                    SkipData(stream, size);
                    var sourcePath = FileSystem.PathCombine(Path.GetDirectoryName(targetFilePath) ?? "", linkTarget);
                    if (File.Exists(sourcePath))
                    {
                        FileSystem.EnsureFilePathExists(targetFilePath);
                        File.Copy(sourcePath, targetFilePath, overwrite: true);
                        extracted.Add(targetFilePath);
                    }
                    return true;
                }
                default:
                    SkipData(stream, size);
                    return true; // unknown entry types (devices, pax global, ...) are skipped
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                 or NotSupportedException)
        {
            error = $"Failed to extract entry {targetFilePath}: {e.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------------ parsing

    private static bool ParseHeader(byte[] block, out string name, out int mode, out long size, out char typeFlag,
                                    out string linkName)
    {
        name = ReadString(block, 0, 100) + ReadString(block, 345, 155); // name + ustar prefix
        mode = ParseOctal(block, 100, 8);
        size = ParseOctal(block, 124, 12);
        typeFlag = block[156] == 0 ? '\0' : (char)block[156];
        linkName = ReadString(block, 157, 100);
        // ustar magic lives at offset 257; anything that still parses as a header is accepted
        // (V7 archives have no magic) — a garbled stream fails on the size/name sanity below.
        return name.Length != 0;
    }

    private static string ReadString(byte[] block, int offset, int count)
    {
        var end = offset;
        var limit = offset + count;
        while (end < limit && block[end] != 0)
        {
            end++;
        }
        return Encoding.UTF8.GetString(block, offset, end - offset).TrimEnd();
    }

    private static int ParseOctal(byte[] block, int offset, int count)
    {
        var value = 0L;
        var started = false;
        for (var i = offset; i < offset + count; i++)
        {
            var b = block[i];
            if (b == 0 || b == (byte)' ')
            {
                if (started)
                {
                    break;
                }
                continue;
            }
            if (b < (byte)'0' || b > (byte)'7')
            {
                break;
            }
            started = true;
            value = (value << 3) + (b - (byte)'0');
        }
        return unchecked((int)value);
    }

    private sealed class PaxOverrides
    {
        public string? Path;

        public string? LinkPath;

        public long? Size;
    }

    /// <summary>Parses "len key=value\n" pax records; keeps prior unknown-less overrides.</summary>
    private static PaxOverrides ParsePax(string payload, PaxOverrides? previous)
    {
        var overrides = new PaxOverrides
        {
            Path = previous?.Path,
            LinkPath = previous?.LinkPath,
            Size = previous?.Size,
        };
        var pos = 0;
        while (pos < payload.Length)
        {
            var space = payload.IndexOf(' ', pos);
            if (space < 0 || !int.TryParse(payload[pos..space], out var len) || len <= 0 || pos + len > payload.Length)
            {
                break;
            }
            var record = payload[(space + 1)..(pos + len)].TrimEnd('\n');
            pos += len;
            var eq = record.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            var key = record[..eq];
            var value = record[(eq + 1)..];
            switch (key)
            {
                case "path":
                    overrides.Path = value;
                    break;
                case "linkpath":
                    overrides.LinkPath = value;
                    break;
                case "size":
                    overrides.Size = long.TryParse(value, out var parsed) ? parsed : null;
                    break;
            }
        }
        return overrides;
    }

    // ------------------------------------------------------------------ stream helpers

    private static string ReadSmallPayload(Stream stream, long size)
    {
        if (size <= 0)
        {
            return "";
        }
        var buffer = new byte[size];
        CopyExactly(stream, buffer, size);
        SkipPadding(stream, size);
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    private static void CopyExactly(Stream source, byte[] target, long count)
    {
        var written = 0;
        while (written < count)
        {
            var read = source.Read(target, written, (int)Math.Min(count - written, target.Length - written));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of tar data");
            }
            written += read;
        }
    }

    private static void CopyExactly(Stream source, Stream target, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, count);
            var read = source.Read(buffer, 0, chunk);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of tar data");
            }
            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    /// <summary>Skips an entry payload of <paramref name="size"/> bytes plus the 512-byte alignment padding.</summary>
    private static void SkipData(Stream stream, long size)
    {
        SkipBytes(stream, size);
        SkipPadding(stream, size);
    }

    /// <summary>Skips the padding that aligns an entry of <paramref name="dataLength"/> bytes to 512.</summary>
    private static void SkipPadding(Stream stream, long dataLength)
    {
        var padding = (BlockSize - dataLength % BlockSize) % BlockSize;
        if (padding > 0)
        {
            SkipBytes(stream, padding);
        }
    }

    /// <summary>
    /// Read-and-discard skip: the tar stream may come out of a GZipStream, which is not
    /// seekable, so Seek cannot be used here.
    /// </summary>
    private static void SkipBytes(Stream stream, long count)
    {
        Span<byte> scratch = stackalloc byte[1024];
        while (count > 0)
        {
            var read = stream.Read(scratch[..(int)Math.Min(scratch.Length, count)]);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of tar data");
            }
            count -= read;
        }
    }

    private static bool ReadBlock(Stream stream, byte[] block)
    {
        var offset = 0;
        while (offset < block.Length)
        {
            var read = stream.Read(block, offset, block.Length - offset);
            if (read == 0)
            {
                return offset == 0; // partial trailing block -> treat as EOF
            }
            offset += read;
        }
        return true;
    }

    private static bool IsZeroBlock(byte[] block)
    {
        foreach (var b in block)
        {
            if (b != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>QUrl::isParentOf equivalent for local paths (same check as MMCZip.ExtractSubDir).</summary>
    private static bool IsParentOf(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent);
        var childFull = Path.GetFullPath(child.TrimEnd('/'));
        if (parentFull.Length == 0)
        {
            return true;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!childFull.StartsWith(parentFull, comparison))
        {
            return false;
        }
        return childFull.Length == parentFull.Length || childFull[parentFull.Length] == Path.DirectorySeparatorChar
            || childFull[parentFull.Length] == '/';
    }
}
