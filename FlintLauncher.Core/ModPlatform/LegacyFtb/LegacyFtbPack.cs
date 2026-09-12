// SPDX-License-Identifier: GPL-3.0-only
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
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public List<string> OldVersions { get; } = [];
    public string CurrentVersion { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Mods { get; set; } = "";
    public string Logo { get; set; } = "";

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
