// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Rachel Powers <508861+Ryex@users.noreply.github.com>
//
// Port of launcher/minecraft/mod/tasks/LocalShaderPackParseTask.{h,cpp} (the
// ShaderPackUtils namespace: 'shaders' folder detection for Optifine/Iris packs).
//
// Deviations from the C++:
//  - LocalShaderPackParseTask (the Task/abort plumbing) is not ported.
//  - Zip inspection uses System.IO.Compression; ArchiveReader::collectFiles(false) +
//    ArchiveReader::exists are mirrored over the entry list (exact entry match or any
//    entry below the directory, case-insensitively).

using System.IO.Compression;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of ShaderPackUtils::ProcessingLevel.</summary>
public enum ShaderPackProcessingLevel
{
    Full,
    BasicInfoOnly,
}

/// <summary>Port of the ShaderPackUtils namespace.</summary>
public static class ShaderPackUtils
{
    /// <summary>Port of ShaderPackUtils::process (dispatch on the resource type).</summary>
    public static bool Process(ShaderPack pack, ShaderPackProcessingLevel level = ShaderPackProcessingLevel.Full)
    {
        switch (pack.Type)
        {
            case ResourceType.Folder:
                return ProcessFolder(pack, level);
            case ResourceType.ZipFile:
                return ProcessZip(pack, level);
            default:
                // qWarning() << "Invalid type for shader pack parse task!";
                return false;
        }
    }

    /// <summary>Port of ShaderPackUtils::processFolder.</summary>
    public static bool ProcessFolder(ShaderPack pack, ShaderPackProcessingLevel level = ShaderPackProcessingLevel.Full)
    {
        var shadersDirInfo = new AbsolutePath(pack.Fileinfo.FullName) / "shaders";
        if (!shadersDirInfo.ExistsDirectory())
        {
            return false; // assets dir does not exists or isn't valid
        }

        pack.PackFormat = ShaderPackFormat.Valid;

        if (level == ShaderPackProcessingLevel.BasicInfoOnly)
        {
            return true; // only need basic info already checked
        }

        return true; // all tests passed
    }

    /// <summary>Port of ShaderPackUtils::processZIP.</summary>
    public static bool ProcessZip(ShaderPack pack, ShaderPackProcessingLevel level = ShaderPackProcessingLevel.Full)
    {
        List<string> files;
        try
        {
            using var zip = ZipFile.OpenRead(pack.Fileinfo.FullName);
            // collectFiles(false): every entry name, files and directories alike
            files = zip.Entries.Select(e => e.FullName).ToList();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false; // can't open zip file
        }

        if (!ZipExists(files, "shaders"))
        {
            // assets dir does not exists at zip root, but shader packs
            // will sometimes be a zip file containing a folder with the
            // actual contents in it. This happens
            // e.g. when the shader pack is downloaded as code
            // from Github. so other than "/shaders", we
            // could also check for a "shaders" folder one level deep.

            // the assumption here is that there is just one
            // folder with the "shader" subfolder. In case
            // there are multiple, the first one is picked.
            var isShaderPresent = files.Any(f => f.Contains("/shaders/", StringComparison.OrdinalIgnoreCase));

            if (!isShaderPresent)
            {
                // assets dir does not exist.
                return false;
            }
        }

        pack.PackFormat = ShaderPackFormat.Valid;

        if (level == ShaderPackProcessingLevel.BasicInfoOnly)
        {
            return true; // only need basic info already checked
        }

        return true;
    }

    /// <summary>Port of ShaderPackUtils::validate.</summary>
    public static bool Validate(string file)
    {
        var sp = new ShaderPack(file);
        return Process(sp, ShaderPackProcessingLevel.BasicInfoOnly) && sp.Valid;
    }

    /// <summary>
    /// Port of ArchiveReader::exists over the collected entry names: an exact entry match
    /// or any entry under that directory (both case-insensitively, like the C++).
    /// </summary>
    private static bool ZipExists(IReadOnlyList<string> files, string path)
    {
        var normalizedPath = path.TrimEnd('/');
        if (normalizedPath.StartsWith('/'))
        {
            normalizedPath = normalizedPath[1..];
        }

        if (normalizedPath.Length == 0 || normalizedPath == ".")
        {
            return true;
        }

        if (normalizedPath == "..")
        {
            return false; // root only
        }

        foreach (var f in files)
        {
            if (f.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var dirPath = normalizedPath + "/";
        foreach (var f in files)
        {
            if (f.StartsWith(dirPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
