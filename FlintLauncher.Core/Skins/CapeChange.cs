// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/skins/CapeChange.{h,cpp}: PUT minecraft/profile/capes/active with
// {"capeId":"<id>"}; an empty cape id becomes a DELETE (cape removal).
using System.Text;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Skins;

public static class CapeChange
{
    /// <summary>Port of CapeChange::make(token, capeId).</summary>
    public static NetRequest Make(string token, string capeId)
    {
        var url = Auth.AuthEndpoints.MinecraftServices.TrimEnd('/') + "/minecraft/profile/capes/active";
        NetRequest request;
        if (capeId.Length == 0)
        {
            // removing the cape is a bare DELETE
            request = new NetRequest(new Uri(url), RequestOptions.NoOptions, HttpMethodValue.Delete);
        }
        else
        {
            var body = "{\"capeId\":\"" + capeId + "\"}";
            request = new NetRequest(new Uri(url), RequestOptions.NoOptions, HttpMethodValue.Put,
                Encoding.UTF8.GetBytes(body));
        }
        request.AddHeader("Authorization", $"Bearer {token}");
        return request;
    }
}
