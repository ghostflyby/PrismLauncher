// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of launcher/minecraft/skins/CapeChange.{h,cpp}: PUT minecraft/profile/capes/active with
// {"capeId":"<id>"}; an empty cape id becomes a DELETE (cape removal).

using System.Text;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Skins;

public static class CapeChange
{
    /// <summary>Port of CapeChange::make(token, capeId).</summary>
    public static NetRequest Make(string token, string capeId, AuthEndpointsOptions? auth = null)
    {
        var url = (auth ?? new AuthEndpointsOptions()).MinecraftServices.TrimEnd('/') + "/minecraft/profile/capes/active";
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
