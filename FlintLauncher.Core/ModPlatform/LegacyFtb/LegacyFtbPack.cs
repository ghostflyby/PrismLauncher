// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of modplatform/legacy_ftb/PackHelpers.h: the legacy FTB modpack description parsed
// from modpacks.xml/thirdparty.xml/{code}.xml (data layer only — the Qt models of the UI
// are not ported).

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

/// <summary>Port of LegacyFTB::PackType.</summary>
public enum PackType : byte
{
    Public,
    ThirdParty,
    Private,
}

/// <summary>Port of LegacyFTB::Modpack.</summary>
public sealed class LegacyFtbModpack
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; set; } = "";
    public List<string> OldVersions { get; } = [];

    // deserialized for xml parity with the legacy FTB pack file
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string CurrentVersion { get; init; } = "";
    public string McVersion { get; init; } = "";
    public string Mods { get; init; } = "";
    public string Logo { get; init; } = "";

    // Technical data
    public string Dir { get; set; } = "";

    /// <summary>The "url" attribute of the xml (the archive file name); the C++ comment
    /// notes the name doesn't make much sense.</summary>
    public string File { get; set; } = "";

    public bool Bugged { get; set; }
    public bool Broken { get; set; }

    public PackType Type { get; set; }
    public string PackCode { get; set; } = "";
}
