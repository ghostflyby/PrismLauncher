// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/tasks/LocalTexturePackParseTask.{h,cpp} (the
// TexturePackUtils namespace: the legacy pack.txt / pack.png texture pack parsing).
//
// Deviations from the C++:
//  - LocalTexturePackParseTask (the Task/abort plumbing) is not ported.
//  - Zip inspection uses System.IO.Compression; the ArchiveReader::parse callback
//    (whose false return makes parse() fail) is mirrored with a plain entry iteration.
//  - pack.png validation reads the PNG header (PngImage.cs) where the C++ decodes any
//    QImage-supported format.
using System.IO.Compression;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of TexturePackUtils::ProcessingLevel.</summary>
public enum TexturePackProcessingLevel
{
    Full,
    BasicInfoOnly,
}

/// <summary>Port of the TexturePackUtils namespace.</summary>
public static class TexturePackUtils
{
    /// <summary>Port of TexturePackUtils::process (dispatch on the resource type).</summary>
    public static bool Process(TexturePack pack, TexturePackProcessingLevel level = TexturePackProcessingLevel.Full)
    {
        switch (pack.Type)
        {
            case ResourceType.Folder:
                return ProcessFolder(pack, level);
            case ResourceType.ZipFile:
                return ProcessZip(pack, level);
            default:
                // qWarning() << "Invalid type for resource pack parse task!";
                return false;
        }
    }

    /// <summary>Port of TexturePackUtils::processFolder.</summary>
    public static bool ProcessFolder(TexturePack pack, TexturePackProcessingLevel level = TexturePackProcessingLevel.Full)
    {
        var mcmetaFileInfo = new FileInfo(FileSystem.PathCombine(pack.Fileinfo.FullName, "pack.txt"));
        if (mcmetaFileInfo.Exists && !mcmetaFileInfo.Attributes.HasFlag(FileAttributes.Directory))
        {
            string data;
            try
            {
                data = File.ReadAllText(mcmetaFileInfo.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException)
            {
                return false; // can't open pack.txt
            }

            var packTxtResult = ProcessPackTXT(pack, data);
            if (!packTxtResult)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (level == TexturePackProcessingLevel.BasicInfoOnly)
        {
            return true;
        }

        var imageFileInfo = new FileInfo(FileSystem.PathCombine(pack.Fileinfo.FullName, "pack.png"));
        if (imageFileInfo.Exists && !imageFileInfo.Attributes.HasFlag(FileAttributes.Directory))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(imageFileInfo.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException)
            {
                return false; // can't open pack.png file
            }

            var packPngResult = ProcessPackPNG(pack, data);
            if (!packPngResult)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>Port of TexturePackUtils::processZIP.</summary>
    public static bool ProcessZip(TexturePack pack, TexturePackProcessingLevel level = TexturePackProcessingLevel.Full)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(pack.Fileinfo.FullName);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return false; // can't open zip file
        }

        using (zip)
        {
            var packProcessed = false;
            var iconProcessed = false;

            // The C++ returns the ArchiveReader::parse() result: a callback returning false
            // (a failed processPackTXT/PNG) makes the whole parse fail; 'stop' breaks the loop.
            foreach (var entry in zip.Entries)
            {
                var filename = entry.FullName;
                var stop = false;
                if (!packProcessed && filename == "pack.txt")
                {
                    packProcessed = true;
                    var data = ReadEntry(entry);
                    stop = packProcessed && (iconProcessed || level == TexturePackProcessingLevel.BasicInfoOnly);
                    var result = ProcessPackTXT(pack, System.Text.Encoding.UTF8.GetString(data));
                    if (!result)
                    {
                        return false;
                    }
                    if (stop)
                    {
                        break;
                    }
                    continue;
                }
                if (!iconProcessed && filename == "pack.png")
                {
                    iconProcessed = true;
                    var data = ReadEntry(entry);
                    stop = packProcessed && iconProcessed;
                    var result = ProcessPackPNG(pack, data);
                    if (!result)
                    {
                        return false;
                    }
                    if (stop)
                    {
                        break;
                    }
                    continue;
                }
                // else: file->skip()
            }
            return true;
        }
    }

    /// <summary>Port of TexturePackUtils::processPackTXT: the description is the raw file content.</summary>
    public static bool ProcessPackTXT(TexturePack pack, string rawData)
    {
        pack.SetDescription(rawData);
        return true;
    }

    /// <summary>Port of TexturePackUtils::processPackPNG(pack, data).</summary>
    public static bool ProcessPackPNG(TexturePack pack, byte[] rawData)
    {
        if (PngImageReader.TryReadPng(rawData, out var img))
        {
            pack.SetImage(img);
        }
        else
        {
            // qWarning() << "Failed to parse pack.png.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Port of TexturePackUtils::processPackPNG(pack) (the reload path). Like upstream,
    /// this always returns false (the ZIP branch even reports "could not set pack.mcmeta
    /// as current file." after success); the only caller ignores the result.
    /// </summary>
    public static bool ProcessPackPNG(TexturePack pack)
    {
        switch (pack.Type)
        {
            case ResourceType.Folder:
            {
                var imageFileInfo = new FileInfo(FileSystem.PathCombine(pack.Fileinfo.FullName, "pack.png"));
                if (imageFileInfo.Exists && !imageFileInfo.Attributes.HasFlag(FileAttributes.Directory))
                {
                    byte[] data;
                    try
                    {
                        data = File.ReadAllBytes(imageFileInfo.FullName);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                             or NotSupportedException)
                    {
                        return false; // can't open pack.png file
                    }

                    if (!ProcessPackPNG(pack, data))
                    {
                        return false; // pack.png invalid
                    }
                }
                else
                {
                    return false; // pack.png does not exists or is not a valid file.
                }
                return false;
            }
            case ResourceType.ZipFile:
            {
                try
                {
                    using var zip = ZipFile.OpenRead(pack.Fileinfo.FullName);
                    var entry = zip.GetEntry("pack.png");
                    if (entry != null)
                    {
                        if (!ProcessPackPNG(pack, ReadEntry(entry)))
                        {
                            return false; // pack.png invalid
                        }
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                         or ArgumentException or NotSupportedException)
                {
                    return false;
                }
                return false; // could not set pack.mcmeta as current file.
            }
            default:
                // qWarning() << "Invalid type for resource pack parse task!";
                return false;
        }
    }

    /// <summary>Port of TexturePackUtils::validate.</summary>
    public static bool Validate(string file)
    {
        var rp = new TexturePack(file);
        return Process(rp, TexturePackProcessingLevel.BasicInfoOnly) && rp.Valid;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
