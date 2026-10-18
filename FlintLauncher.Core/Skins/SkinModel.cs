// SPDX-License-Identifier: GPL-3.0-only
// Port of the persistence half of launcher/minecraft/skins/SkinModel.{h,cpp} and
// SkinList.{h,cpp}: the index.json layout of the skins folder.
//
// The index is the typed SkinIndexJson wire (docs/JSON-MIGRATION.md, the Qt write-tail
// batch): written through the source-generated context, read back through it. The C++
// per-key probing degenerated to "the whole list is empty" on any wrong-kind value (the
// GetValue<string> throw inside the constructor hit the outer catch) — the typed read
// keeps exactly that whole-file failure behavior, and the mapper keeps the "SLIM or
// nothing is CLASSIC" model read.
//
// Deviations:
//  - SkinList is a QAbstractListModel tied to QImage previews and the watched skins directory;
//    the port keeps the data (path, name, cape id, url, model) and the index.json load/save.
//  - SkinModel::isValid() validates the PNG texture dimensions via QImage; that decode is not
//    ported, so a stored skin is considered valid when it has a name (the file existence part of
//    the check stays in the UI layer).

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.Skins;

public enum SkinModelKind
{
    Classic,
    Slim,
}

/// <summary>The index.json wire: {"skins": [...]}, camelCase members.</summary>
internal sealed record SkinIndexJson
{
    public SkinEntryJson[]? Skins { get; init; }
}

/// <summary>One index entry; the wire model is the "CLASSIC"/"SLIM" string and anything
/// else reads CLASSIC (the C++ comparison).</summary>
internal sealed record SkinEntryJson
{
    public string? Name { get; init; }

    public string? CapeId { get; init; }

    public string? Url { get; init; }

    public string? Model { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SkinIndexJson))]
internal sealed partial class SkinJsonContext : JsonSerializerContext;

/// <summary>Data subset of SkinModel.</summary>
public sealed class SkinModel
{
    public SkinModel() { }

    public SkinModel(string skinsDir, string name, string capeId = "", string url = "",
        SkinModelKind model = SkinModelKind.Classic)
    {
        Path = System.IO.Path.Combine(skinsDir, name + ".png");
        CapeId = capeId;
        Url = url;
        Model = model;
    }

    /// <summary>The wire entry → model (the C++ SkinModel(QJsonObject) read).</summary>
    internal static SkinModel FromWire(string skinsDir, SkinEntryJson entry)
    {
        return new SkinModel(skinsDir, entry.Name ?? "", entry.CapeId ?? "", entry.Url ?? "",
            entry.Model == "SLIM" ? SkinModelKind.Slim : SkinModelKind.Classic);
    }

    /// <summary>The model → wire entry (the C++ toJson).</summary>
    internal SkinEntryJson ToWire()
    {
        return new SkinEntryJson
        {
            Name = Name(),
            CapeId = CapeId,
            Url = Url,
            Model = GetModelString(),
        };
    }

    public string Path = "";

    public string CapeId { get; set; } = "";

    public SkinModelKind Model { get; set; } = SkinModelKind.Classic;

    public string Url { get; set; } = "";

    /// <summary>QFileInfo::completeBaseName of the path.</summary>
    public string Name()
    {
        var fileName = System.IO.Path.GetFileName(Path);
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    public string GetModelString() => Model switch
    {
        SkinModelKind.Classic => "CLASSIC",
        SkinModelKind.Slim => "SLIM",
        _ => "",
    };
}

/// <summary>Persistence half of SkinList: the &{"skins": [...]} index.json in the skins folder.</summary>
public static class SkinListStore
{
    public static List<SkinModel> Load(string skinsDir)
    {
        var newSkins = new List<SkinModel>();
        var manifestPath = Path.Combine(skinsDir, "index.json");
        if (!File.Exists(manifestPath))
        {
            return newSkins;
        }

        try
        {
            var root = JsonSerializer.Deserialize(FileSystem.Read(manifestPath), SkinJsonContext.Default.SkinIndexJson);
            foreach (var entry in root?.Skins ?? [])
            {
                var model = SkinModel.FromWire(skinsDir, entry);
                if (IsValid(model))
                {
                    newSkins.Add(model);
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            // qCritical() << "Couldn't load skins json:" << e.cause();
        }

        return newSkins;
    }

    public static void Save(string skinsDir, IReadOnlyList<SkinModel> skins)
    {
        var doc = new SkinIndexJson
        {
            Skins = skins.Select(skin => skin.ToWire()).ToArray(),
        };
        FileSystem.Write(Path.Combine(skinsDir, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(doc, SkinJsonContext.Default.SkinIndexJson));
    }

    private static bool IsValid(SkinModel skin)
    {
        // upstream: non-null texture with 64x32/64x64 dimensions (QImage decode not ported)
        return skin.Name().Length != 0;
    }
}
