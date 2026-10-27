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
//  - Upstream builds the request through QHttpMultiPart on QNetworkAccessManager; the port
//    pre-builds an identical multipart body (same part headers, CRLF framing) and sends it
//    with an injected client (defaulting to the auth options' client). This stays a
//    FlintTask like the C++ Net::Request subclass. The multipart Content-Type must be set on
//    the content headers, which the plain fetch helper cannot do.
//  - QHttpMultiPart generates a random boundary of a different shape — any legal boundary is
//    equivalent, so the port uses a "flint-<32 hex>" one.
//  - The endpoint base goes through the injected AuthEndpointsOptions.MinecraftServices
//    (defaults byte-identical to the hardcoded upstream URL) so tests can stub it.

using System.Text;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;

namespace FlintLauncher.Core.Skins;

public sealed class SkinUpload : FlintTask
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

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var boundary = "flint-" + Guid.NewGuid().ToString("N");
        var body = BuildMultipartBody(boundary, _skinData, _variant);

        EmitStatus("Uploading skin");
        // the inactivity window (RequestTimeout): the transport's retry gate arms it with
        // its configured length and covers the send with it
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_auth.MinecraftServices.TrimEnd('/') + "/minecraft/profile/skins"));
        using var window = new RetryHandler.Window(message, cancellationToken);
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
            window.Token).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        if (statusCode is not 200 and not 201 and not 202 and not 203 and not 204 and not 206)
        {
            EmitFailed($"HTTP status {statusCode} for {message.RequestUri}");
            return;
        }

        EmitSucceeded();
    }

    /// <summary>
    /// The exact QHttpMultiPart(QHttpMultiPart::FormDataType) framing: the skin part carries
    /// "Content-Type: image/png" then "Content-Disposition: form-data; name=&quot;file&quot;;
    /// filename=&quot;skin.png&quot;", the model part only the Content-Disposition line.
    /// </summary>
    public static byte[] BuildMultipartBody(string boundary, byte[] skinData, string variant)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Type: image/png\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Disposition: form-data; name=\"file\"; filename=\"skin.png\"\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(skinData);
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Disposition: form-data; name=\"variant\"\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.UTF8.GetBytes(variant));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "--\r\n"));
        return stream.ToArray();
    }
}
