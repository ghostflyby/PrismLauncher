// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/screenshots/Screenshot.h: one screenshot in the instance's
// screenshots folder plus the imgur metadata filled in after an upload.
//
// Deviations: the C++ stores a QFileInfo; the port stores the path string (the helpers the
// UI needs — fileName/baseName — are provided here, matching QFileInfo semantics).

using TruePath;

namespace FlintLauncher.Core.Screenshots;

public sealed class ScreenShot(AbsolutePath filePath)
{
    /// <summary>Port of m_file (QFileInfo::absoluteFilePath).</summary>
    public AbsolutePath FilePath { get; } = filePath;

    /// <summary>Link to the uploaded image (m_url); empty until the upload succeeded.</summary>
    public string Url { get; set; } = "";

    /// <summary>The imgur image id (m_imgurId).</summary>
    public string ImgurId { get; set; } = "";

    /// <summary>The imgur delete hash (m_imgurDeleteHash).</summary>
    public string ImgurDeleteHash { get; set; } = "";

    /// <summary>Port of QFileInfo::fileName().</summary>
    public string FileName() => FilePath.FileName;

    /// <summary>Port of QFileInfo::baseName(): everything up to (not including) the first '.'.</summary>
    public string BaseName()
    {
        var name = FileName();
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }
}
