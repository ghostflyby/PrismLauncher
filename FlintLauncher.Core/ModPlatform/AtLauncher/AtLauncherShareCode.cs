// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLShareCode.{h,cpp}: the {AtlApiBaseUrl}share-codes/{code}
// response model (the optional-mod selection encoded by the ATLauncher website). Reads go
// through the JsonNode model (single-DOM, the JsonUtils node-side family).
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

public sealed class ShareCodeMod
{
    public bool Selected { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ShareCode
{
    public string Pack { get; set; } = "";
    public string Version { get; set; } = "";
    public List<ShareCodeMod> Mods { get; } = [];
}

public sealed class ShareCodeResponse
{
    public bool Error { get; set; }
    public int Code { get; set; }
    public string Message { get; set; } = "";
    public ShareCode Data { get; } = new();
}

public static class AtLauncherShareCode
{
    private static void LoadShareCodeMod(ShareCodeMod m, JsonNode obj)
    {
        m.Selected = JsonUtils.RequireBoolean(obj, "selected", "'selected'");
        m.Name = JsonUtils.RequireString(obj, "name", "'name'");
    }

    private static void LoadShareCode(ShareCode c, JsonNode obj)
    {
        c.Pack = JsonUtils.RequireString(obj, "pack", "'pack'");
        c.Version = JsonUtils.RequireString(obj, "version", "'version'");

        var mods = (JsonNode)JsonUtils.RequireObject(obj, "mods", "'mods'");
        var optional = JsonUtils.RequireArray(mods, "optional", "'optional'");
        foreach (var modRaw in optional)
        {
            var mod = new ShareCodeMod();
            LoadShareCodeMod(mod, AtLauncherJson.Object(modRaw));
            c.Mods.Add(mod);
        }
    }

    /// <summary>Port of ATLauncher::loadShareCodeResponse.</summary>
    public static void LoadShareCodeResponse(ShareCodeResponse r, JsonNode obj)
    {
        r.Error = JsonUtils.RequireBoolean(obj, "error", "'error'");
        r.Code = JsonUtils.RequireInteger(obj, "code", "'code'");

        if (obj["message"] is JsonValue messageValue
            && messageValue.GetValueKind() != System.Text.Json.JsonValueKind.Null)
        {
            r.Message = JsonUtils.RequireString(obj, "message", "'message'");
        }

        if (!r.Error)
        {
            var dataRaw = JsonUtils.RequireObject(obj, "data", "'data'");
            LoadShareCode(r.Data, dataRaw);
        }
    }
}
