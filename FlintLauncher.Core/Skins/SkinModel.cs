// SPDX-License-Identifier: GPL-3.0-only
// Port of the persistence half of launcher/minecraft/skins/SkinModel.{h,cpp} and
// SkinList.{h,cpp}: the index.json layout of the skins folder.
//
// Deviations:
//  - SkinList is a QAbstractListModel tied to QImage previews and the watched skins directory;
//    the port keeps the data (path, name, cape id, url, model) and the index.json load/save.
//  - SkinModel::isValid() validates the PNG texture dimensions via QImage; that decode is not
//    ported, so a stored skin is considered valid when it has a name (the file existence part of
//    the check stays in the UI layer).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.Skins;

public enum SkinModelKind
{
    Classic,
    Slim,
}

/// <summary>Data subset of SkinModel.</summary>
public sealed class SkinModel
{
    public SkinModel()
    {
    }

    public SkinModel(string skinsDir, JsonObject obj)
    {
        CapeId = obj["capeId"]?.GetValue<string>() ?? "";
        Url = obj["url"]?.GetValue<string>() ?? "";
        Model = obj["model"]?.GetValue<string>() == "SLIM" ? SkinModelKind.Slim : SkinModelKind.Classic;
        var name = obj["name"]?.GetValue<string>() ?? "";
        Path = System.IO.Path.Combine(skinsDir, name + ".png");
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

    public JsonObject ToJson() => new()
    {
        ["name"] = Name(),
        ["capeId"] = CapeId,
        ["url"] = Url,
        ["model"] = GetModelString(),
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
            using var doc = JsonDocument.Parse(FileSystem.Read(manifestPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("skins", out var skins) ||
                skins.ValueKind != JsonValueKind.Array)
            {
                return newSkins;
            }
            foreach (var skin in skins.EnumerateArray())
            {
                if (skin.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var node = JsonNode.Parse(skin.GetRawText());
                if (node is JsonObject obj)
                {
                    var model = new SkinModel(skinsDir, obj);
                    if (IsValid(model))
                    {
                        newSkins.Add(model);
                    }
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
        var doc = new JsonObject();
        var arr = new JsonArray();
        foreach (var skin in skins)
        {
            arr.Add((JsonNode?)skin.ToJson());
        }
        doc["skins"] = arr;
        // upstream goes through Json::write() (QJsonDocument::Indented): sorted keys, 4-space indent
        FileSystem.Write(Path.Combine(skinsDir, "index.json"),
            Encoding.UTF8.GetBytes(QtJson.ToIndentedString(doc)));
    }

    private static bool IsValid(SkinModel skin)
    {
        // upstream: non-null texture with 64x32/64x64 dimensions (QImage decode not ported)
        return skin.Name().Length != 0;
    }
}
