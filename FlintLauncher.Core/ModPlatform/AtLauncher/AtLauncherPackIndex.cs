// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLPackIndex.{h,cpp}: the packsnew.json index model
// (IndexedPack/IndexedVersion) and its parser. The safeName is the C++ normalized pack
// image name ("[^A-Za-z0-9]" stripped, lowercased, ".png" appended). Reads go through the
// JsonNode model (single-DOM, the JsonUtils node-side family).

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Json;

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
    private static void LoadIndexedVersion(IndexedVersion v, JsonNode obj)
    {
        v.Version = JsonUtils.RequireString(obj, "version", "'version'");
        v.Minecraft = JsonUtils.RequireString(obj, "minecraft", "'minecraft'");
    }

    /// <summary>Port of ATLauncher::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack m, JsonNode obj)
    {
        m.Id = JsonUtils.RequireInteger(obj, "id", "'id'");
        m.Position = JsonUtils.RequireInteger(obj, "position", "'position'");
        m.Name = JsonUtils.RequireString(obj, "name", "'name'");
        m.Type = JsonUtils.RequireString(obj, "type", "'type'") == "private" ? PackType.Private : PackType.Public;
        var versionsArr = JsonUtils.RequireArray(obj, "versions", "'versions'");
        foreach (var versionRaw in versionsArr)
        {
            var version = new IndexedVersion();
            LoadIndexedVersion(version, AtLauncherJson.Object(versionRaw));
            m.Versions.Add(version);
        }
        m.System = JsonUtils.EnsureBoolean(obj["system"]);
        m.Description = JsonUtils.EnsureString(obj["description"]);

        m.SafeName = SafePackName(JsonUtils.RequireString(obj, "name", "'name'")).ToLowerInvariant() + ".png";
    }

    /// <summary>The "[^A-Za-z0-9]" -> "" replacement shared with the install task's packSafeName.</summary>
    public static string SafePackName(string name) => UnsafeCharsRegex().Replace(name, "");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex UnsafeCharsRegex();
}
