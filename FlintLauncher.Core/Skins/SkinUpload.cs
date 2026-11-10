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
// Port of launcher/minecraft/skins/SkinUpload.{h,cpp}: POST multipart/form-data to
// minecraft/profile/skins with a "file" (image/png) part and a "variant" part.
//
// Deviations:
//  - Upstream builds the request through QHttpMultiPart on QNetworkAccessManager; the
//    request is the account service client's skin upload operation (a multipart/form-data
//    body), sent with an injected client (defaulting to the auth
//    context's client), like the C++ Net::Request subclass.
//  - The endpoint base goes through the injected auth context's Minecraft services base
//    (defaults byte-identical to the hardcoded upstream URL) so tests can stub it.

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Net;
using FlintLauncher.ExternalApi.Mojang;
using TruePath;

namespace FlintLauncher.Core.Skins;

public sealed class SkinUpload
{
    private readonly string _token;
    private readonly byte[] _skinData;
    private readonly string _variant;
    private readonly AuthEndpointsOptions _auth;
    private readonly HttpClient _http;

    private SkinUpload(string token, byte[] skinData, string variant, AuthEndpointsOptions? auth,
        HttpClient? http)
    {
        _token = token;
        _skinData = skinData;
        _variant = variant;
        _auth = auth ?? new AuthEndpointsOptions();
        _http = http ?? _auth.Http;
    }

    /// <summary>Port of SkinUpload::make(token, path, variant) — the file is read eagerly.</summary>
    public static SkinUpload Make(string token, AbsolutePath path, string variant,
        AuthEndpointsOptions? auth = null, HttpClient? http = null)
    {
        return Make(token, File.ReadAllBytes(path.Value), variant, auth, http);
    }

    /// <summary>Body-bytes variant (FS::read(m_path) upstream).</summary>
    public static SkinUpload Make(string token, byte[] skinData, string variant,
        AuthEndpointsOptions? auth = null, HttpClient? http = null)
    {
        return new SkinUpload(token, skinData, variant, auth, http);
    }

    /// <summary>Posts the skin; a failure — a non-2xx answer carrying the status and the
    /// request URL among them — surfaces as a <see cref="MojangApiException"/>.</summary>
    public Task SendAsync(CancellationToken cancellationToken = default) =>
        _auth.Mojang.MinecraftServices.UploadSkin(_token, _skinData, _variant)
            .SendOnceAsync(_http, cancellationToken);
}
