// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLPackIndex.{h,cpp}: the packsnew.json index model
// (IndexedPack/IndexedVersion) and its parser. The safeName is the C++ normalized pack
// image name ("[^A-Za-z0-9]" stripped, lowercased, ".png" appended).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

public sealed class IndexedVersion
{
    public string Version { get; set; } = "";
    public string Minecraft { get; set; } = "";
}

public sealed class IndexedPack
{
    public int Id { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public PackType Type { get; set; }
    public List<IndexedVersion> Versions { get; } = [];
    public bool System { get; set; }
    public string Description { get; set; } = "";

    public string SafeName { get; set; } = "";
}

public static partial class AtLauncherPackIndex
{
    private static void LoadIndexedVersion(IndexedVersion v, JsonObject obj)
    {
        v.Version = AtLauncherJson.RequireString(obj, "version");
        v.Minecraft = AtLauncherJson.RequireString(obj, "minecraft");
    }

    /// <summary>Port of ATLauncher::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack m, JsonObject obj)
    {
        m.Id = AtLauncherJson.RequireInteger(obj, "id");
        m.Position = AtLauncherJson.RequireInteger(obj, "position");
        m.Name = AtLauncherJson.RequireString(obj, "name");
        m.Type = AtLauncherJson.RequireString(obj, "type") == "private" ? PackType.Private : PackType.Public;
        var versionsArr = AtLauncherJson.RequireArray(obj, "versions");
        foreach (var versionRaw in versionsArr)
        {
            var version = new IndexedVersion();
            LoadIndexedVersion(version, AtLauncherJson.Object(versionRaw));
            m.Versions.Add(version);
        }
        m.System = AtLauncherJson.OptBool(obj, "system");
        m.Description = AtLauncherJson.OptString(obj, "description");

        m.SafeName = SafePackName(AtLauncherJson.RequireString(obj, "name")).ToLowerInvariant() + ".png";
    }

    /// <summary>The "[^A-Za-z0-9]" -> "" replacement shared with the install task's packSafeName.</summary>
    public static string SafePackName(string name) => UnsafeCharsRegex().Replace(name, "");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex UnsafeCharsRegex();
}
