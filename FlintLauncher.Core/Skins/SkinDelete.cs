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
// Port of launcher/minecraft/skins/SkinDelete.{h,cpp}: DELETE minecraft/profile/skins/active.

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Skins;

public static class SkinDelete
{
    /// <summary>Port of SkinDelete::delete(token); failures surface as
    /// <see cref="FlintHttpException"/> (the old NetRequest status gate).</summary>
    public static Task DeleteAsync(string token, AuthEndpointsOptions? auth = null,
        CancellationToken cancellationToken = default)
    {
        var options = auth ?? new AuthEndpointsOptions();
        var url = options.MinecraftServices.TrimEnd('/') + "/minecraft/profile/skins/active";
        var message = new HttpRequestMessage(HttpMethod.Delete, url);
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return FlintHttp.SendAsync(options.Http, message, cancellationToken);
    }
}
