// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLPackIndex.{h,cpp}: the packsnew.json index model
// (IndexedPack/IndexedVersion) and its parser. The safeName is the C++ normalized pack
// image name ("[^A-Za-z0-9]" stripped, lowercased, ".png" appended). The wire shape is
// annotated on the model properties; deserialization goes through the AtLauncherJsonContext
// (AtLauncherApi.cs).

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

public sealed class IndexedVersion
{
    [JsonPropertyName("version")]
    public required string Version { get; set; }

    [JsonPropertyName("minecraft")]
    public required string Minecraft { get; set; }
}

public sealed class IndexedPack
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("position")]
    public required int Position { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    // the wire value is the raw "private"/"public" text; PackType stays the computed view
    [JsonPropertyName("type")]
    public required string TypeRaw { get; set; }

    [JsonPropertyName("versions")]
    public required List<IndexedVersion> Versions { get; init; }

    [JsonPropertyName("system")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool System { get; set; }

    [JsonPropertyName("description")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Description { get; set; } = "";

    public PackType Type => TypeRaw == "private" ? PackType.Private : PackType.Public;

    public string SafeName => AtLauncherPackIndex.SafePackName(Name).ToLowerInvariant() + ".png";
}

public static partial class AtLauncherPackIndex
{
    /// <summary>The "[^A-Za-z0-9]" -> "" replacement shared with the install task's packSafeName.</summary>
    public static string SafePackName(string name) => UnsafeCharsRegex().Replace(name, "");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex UnsafeCharsRegex();
}
