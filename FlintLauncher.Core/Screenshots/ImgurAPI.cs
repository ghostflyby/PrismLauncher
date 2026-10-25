// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
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
// Port of launcher/screenshots/ImgurAPI.{h,cpp} (the unified imgur request layer that
// replaced ImgurUpload.{h,cpp} + ImgurAlbumCreation.{h,cpp}): POST one screenshot to imgur
// as multipart/form-data, and group uploaded screenshots into an album through the
// application/x-www-form-urlencoded delete hashes. Upstream exposes ImgurAPI::makeUpload /
// ImgurAPI::makeAlbum returning (request, result) pairs; the request half of each pair is a
// Net::Request the caller starts, so here the factories return the started-by-caller task
// that performs the identical HTTP exchange.
//
// Deviations:
//  - Upstream builds the requests through QHttpMultiPart on QNetworkAccessManager; the port
//    pre-builds an identical multipart body (same part headers and CRLF framing, like
//    Skins/SkinUpload.cs) and sends it through the injected client, because
//    NetRequest cannot express multipart Content-Type headers.
//  - The imgur base URL / client id come from FlintConfig (BuildConfig.IMGUR_BASE_URL /
//    IMGUR_CLIENT_ID) by default but are factory-injectable so tests can stub them.

using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Screenshots;

/// <summary>The imgur response envelope {"success": true, "data": {...}} — shared by the
/// image upload and album creation parse functions (the RPC::Sink payloads).</summary>
public sealed record ImgurResponseJson(
    bool? Success,
    ImgurDataJson? Data);

public sealed record ImgurDataJson(
    string? Id,
    string? Link,
    // all-lowercase on the wire — camelCase would produce "deleteHash"
    [property: JsonPropertyName("deletehash")]
    string? DeleteHash);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImgurResponseJson))]
internal sealed partial class ImgurJsonContext : JsonSerializerContext;

/// <summary>Port of the ImgurAPI namespace: the two imgur request factories. An instance
/// rides the injected client pair (LauncherRuntime.Imgur — same de-singletoning story as the
/// platform clients); the tasks it builds send through that stack's client.</summary>
public sealed class ImgurApi(HttpClient http, NetSettings settings)
{
    /// <summary>The network stack this client's uploads flow through.</summary>
    public HttpClient Http { get; } = http;

    public NetSettings Settings { get; } = settings;

    /// <summary>Port of ImgurAPI::AlbumResult.</summary>
    public sealed record AlbumResult
    {
        public string DeleteHash { get; set; } = "";
        public string Id { get; set; } = "";
    }

    /// <summary>The imgur image endpoint: BuildConfig.IMGUR_BASE_URL + "image".</summary>
    public static string UploadUrl(string? baseUrl = null) => (baseUrl ?? FlintConfig.Default.ImgurBaseUrl) + "image";

    /// <summary>The imgur album endpoint: BuildConfig.IMGUR_BASE_URL + "album".</summary>
    public static string AlbumUrl(string? baseUrl = null) => (baseUrl ?? FlintConfig.Default.ImgurBaseUrl) + "album";

    /// <summary>Port of ImgurAPI::makeUpload: upload one screenshot as an image.</summary>
    public ImgurUpload MakeUpload(ScreenShot shot, string? baseUrl = null, string? clientId = null) =>
        new(shot, Http, Settings, baseUrl, clientId);

    /// <summary>Port of ImgurAPI::makeAlbum: group uploaded screenshots into an album
    /// (the payload needs the delete hashes left by the earlier uploads).</summary>
    public ImgurAlbumCreation MakeAlbum(AlbumResult result, IReadOnlyList<ScreenShot> screenshots,
        string? baseUrl = null, string? clientId = null) =>
        new(result, screenshots, Http, Settings, baseUrl, clientId);
}

/// <summary>The upload half of ImgurAPI::makeUpload (the Net::Request + RPC::Sink&lt;QString&gt;
/// pair): fills the ScreenShot metadata from the response JSON.</summary>
public sealed class ImgurUpload(
    ScreenShot shot,
    HttpClient http,
    NetSettings settings,
    string? baseUrl = null,
    string? clientId = null)
    : FlintTask
{
    private readonly string _clientId = clientId ?? FlintConfig.Default.ImgurClientId;
    private readonly HttpClient _http = http;
    private readonly NetSettings _settings = settings;

    /// <summary>The imgur endpoint: BuildConfig.IMGUR_BASE_URL + "image".</summary>
    public string EndpointUrl
    {
        get => field + "image";
    } = baseUrl ?? FlintConfig.Default.ImgurBaseUrl;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        byte[] fileData;
        try
        {
            fileData = await shot.FilePath.ReadAllBytesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or SecurityException)
        {
            EmitFailed($"Could not open file {shot.FilePath} for reading: {e.Message}");
            return;
        }

        var boundary = "flint-" + Guid.NewGuid().ToString("N");
        var body = BuildMultipartBody(boundary, fileData, shot.FileName(), shot.BaseName());

        using var message = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        message.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {_clientId}");
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        string responseBody;
        try
        {
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            responseBody = "";
        }

        if (statusCode is not 200 and not 201 and not 202 and not 203 and not 206)
        {
            EmitFailed($"HTTP status {statusCode} for {EndpointUrl}\n{Truncate(responseBody)}");
            return;
        }

        // Port of the RPC::Sink<QString> parse function.
        ImgurResponseJson? reply;
        try
        {
            reply = JsonSerializer.Deserialize(responseBody, ImgurJsonContext.Default.ImgurResponseJson);
        }
        catch (JsonException)
        {
            // "imgur server did not reply with JSON"
            EmitFailed("Invalid json reply");
            return;
        }

        if (reply?.Success != true)
        {
            EmitFailed("Screenshot was not uploaded successfully");
            return;
        }

        if (reply.Data is { } data)
        {
            shot.ImgurId = data.Id ?? "";
            shot.Url = data.Link ?? "";
            shot.ImgurDeleteHash = data.DeleteHash ?? "";
        }

        EmitSucceeded();
    }

    /// <summary>
    /// The exact QHttpMultiPart(QHttpMultiPart::FormDataType) framing: the image part carries
    /// "Content-Type: image/png" then the Content-Disposition with the file name, followed by
    /// the "type" = "file" part and the "title" = baseName part.
    /// </summary>
    public static byte[] BuildMultipartBody(string boundary, byte[] imageData, string fileName, string baseName)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Type: image/png\r\n"));
        stream.Write(Encoding.Latin1.GetBytes(
            "Content-Disposition: form-data; name=\"image\"; filename=\"" + fileName + "\"\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(imageData);
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Disposition: form-data; name=\"type\"\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.UTF8.GetBytes("file"));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Disposition: form-data; name=\"title\"\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.UTF8.GetBytes(baseName));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "--\r\n"));
        return stream.ToArray();
    }

    private static string Truncate(string body) => body.Length > 4096 ? body[..4096] : body;
}

/// <summary>The album half of ImgurAPI::makeAlbum (the Net::Request + RPC::Sink&lt;AlbumResult&gt;
/// pair): posts the uploaded screenshots' delete hashes.</summary>
public sealed class ImgurAlbumCreation(
    ImgurApi.AlbumResult result,
    IReadOnlyList<ScreenShot> screenshots,
    HttpClient http,
    NetSettings settings,
    string? baseUrl = null,
    string? clientId = null)
    : FlintTask
{
    private readonly string _clientId = clientId ?? FlintConfig.Default.ImgurClientId;
    private readonly HttpClient _http = http;
    private readonly NetSettings _settings = settings;

    /// <summary>The imgur endpoint: BuildConfig.IMGUR_BASE_URL + "album".</summary>
    public string EndpointUrl
    {
        get => field + "album";
    } = baseUrl ?? FlintConfig.Default.ImgurBaseUrl;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Port of getPayload(): "deletehashes=" + hashes.join("&deletehashes=")
        //                     + "&title=Minecraft%20Screenshots&privacy=hidden"
        var hashes = screenshots.Select(shot => shot.ImgurDeleteHash).ToArray();
        var data = "deletehashes=" + string.Join("&deletehashes=", hashes) +
                   "&title=Minecraft%20Screenshots&privacy=hidden";

        using var message = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        message.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {_clientId}");
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        // HttpClient emits Content-Type only from the content headers.
        message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        string responseBody;
        try
        {
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            responseBody = "";
        }

        if (statusCode is not 200 and not 201 and not 202 and not 203 and not 206)
        {
            EmitFailed($"HTTP status {statusCode} for {EndpointUrl}");
            return;
        }

        // Port of the RPC::Sink<AlbumResult> parse function.
        ImgurResponseJson? reply;
        try
        {
            reply = JsonSerializer.Deserialize(responseBody, ImgurJsonContext.Default.ImgurResponseJson);
        }
        catch (JsonException)
        {
            EmitFailed("Invalid json reply");
            return;
        }

        if (reply?.Success != true)
        {
            EmitFailed("Failed to create album");
            return;
        }

        if (reply.Data is { } albumData)
        {
            result.DeleteHash = albumData.DeleteHash ?? "";
            result.Id = albumData.Id ?? "";
        }

        EmitSucceeded();
    }
}
