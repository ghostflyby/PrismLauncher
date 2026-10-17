// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/skins/SkinDelete.{h,cpp}: DELETE minecraft/profile/skins/active.

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Skins;

public static class SkinDelete
{
    /// <summary>Port of SkinDelete::make(token).</summary>
    public static NetRequest Make(string token, AuthEndpointsOptions? auth = null)
    {
        var url = (auth ?? new AuthEndpointsOptions()).MinecraftServices.TrimEnd('/') + "/minecraft/profile/skins/active";
        var request = new NetRequest(new Uri(url), RequestOptions.NoOptions, HttpMethodValue.Delete);
        request.AddHeader("Authorization", $"Bearer {token}");
        return request;
    }
}
