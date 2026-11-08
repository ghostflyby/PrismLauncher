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
// The request halves of launcher/screenshots/ImgurAPI's makeUpload / makeAlbum: one image
// as multipart/form-data (a pre-built body with the QHttpMultiPart part headers and CRLF
// framing, since the multipart Content-Type must ride the content headers) and the album
// as application/x-www-form-urlencoded delete hashes. Both replies keep the imgur status
// rule (200/201/202/203/206 pass) and the "success" envelope check; their failures carry
// the upstream texts as InvalidOperationException inside the wrapped failure.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FlintLauncher.ExternalApi.Imgur;

/// <summary>The shared request headers and reply rules of the imgur operations.</summary>
internal static class ImgurRequests
{
    internal static HttpRequestMessage WithClientId(this HttpRequestMessage request, ImgurClientOptions options)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {options.ClientId}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    /// <summary>The reply body; a body read that fails contributes "" (the status and the
    /// envelope checks below carry the failure).</summary>
    internal static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return "";
        }
    }

    internal static bool IsAccepted(HttpResponseMessage response) =>
        (int)response.StatusCode is 200 or 201 or 202 or 203 or 206;

    /// <summary>The RPC::Sink parse: a non-JSON reply is "Invalid json reply", an envelope
    /// without success:true is <paramref name="failureMessage"/>.</summary>
    internal static ImgurDataJson? ReadEnvelope(string body, string failureMessage)
    {
        ImgurResponseJson? reply;
        try
        {
            reply = JsonSerializer.Deserialize(body, ImgurJsonContext.Default.ImgurResponseJson);
        }
        catch (JsonException)
        {
            // "imgur server did not reply with JSON"
            throw new InvalidOperationException("Invalid json reply");
        }

        if (reply?.Success != true)
        {
            throw new InvalidOperationException(failureMessage);
        }

        return reply.Data;
    }

    internal static string Truncate(string body) =>
        body.Length > ApiResponses.MaxErrorExcerptLength ? body[..ApiResponses.MaxErrorExcerptLength] : body;
}

// ------------------------------------------------------------------------------ images

/// <summary>POST {base}image — one image upload; the reply's data carries the image id, link
/// and delete hash (null when the reply has no data object).</summary>
public readonly record struct ImgurImageUploadQuery(
    ImgurClientOptions Options,
    byte[] ImageData,
    string FileName,
    string Title) : IApiOperation<ImgurDataJson?>
{
    /// <summary>The image endpoint URL.</summary>
    public string Url => Options.BaseUrl + "image";

    public HttpRequestMessage CreateRequest()
    {
        var content = new MultipartFormDataContent("flint-" + Guid.NewGuid().ToString("N"));
        var image = new ByteArrayContent(ImageData);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(image, "image", FileName);
        content.Add(new StringContent("file"), "type");
        content.Add(new StringContent(Title), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
        return request.WithClientId(Options);
    }

    public Exception WrapFailure(Exception failure) => new ImgurApiException(failure.Message, failure);

    public async Task<ImgurDataJson?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ImgurRequests.ReadBodyAsync(response, ct).ConfigureAwait(false);
        if (!ImgurRequests.IsAccepted(response))
        {
            throw new InvalidOperationException(
                $"HTTP status {(int)response.StatusCode} for {Url}\n{ImgurRequests.Truncate(body)}");
        }

        return ImgurRequests.ReadEnvelope(body, "Screenshot was not uploaded successfully");
    }

}

// ------------------------------------------------------------------------------ albums

/// <summary>POST {base}album — groups uploaded images into a hidden album through their
/// delete hashes; the reply's data carries the album id and delete hash.</summary>
public readonly record struct ImgurAlbumCreationQuery(
    ImgurClientOptions Options,
    IReadOnlyList<string> DeleteHashes) : IApiOperation<ImgurDataJson?>
{
    /// <summary>The album endpoint URL.</summary>
    public string Url => Options.BaseUrl + "album";

    public HttpRequestMessage CreateRequest()
    {
        // getPayload(): the delete hashes repeat the deletehashes field, plus the fixed
        // title and the hidden privacy
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new FormUrlEncodedContent(DeleteHashes.Select(
                    hash => new KeyValuePair<string, string>("deletehashes", hash))
                .Append(new KeyValuePair<string, string>("title", "Minecraft Screenshots"))
                .Append(new KeyValuePair<string, string>("privacy", "hidden"))),
        };
        return request.WithClientId(Options);
    }

    public Exception WrapFailure(Exception failure) => new ImgurApiException(failure.Message, failure);

    public async Task<ImgurDataJson?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ImgurRequests.ReadBodyAsync(response, ct).ConfigureAwait(false);
        if (!ImgurRequests.IsAccepted(response))
        {
            throw new InvalidOperationException($"HTTP status {(int)response.StatusCode} for {Url}");
        }

        return ImgurRequests.ReadEnvelope(body, "Failed to create album");
    }
}

// ------------------------------------------------------------------------------ groups

/// <summary>The image endpoint of <see cref="ImgurClient"/>.</summary>
public readonly struct ImgurImageQueries(ImgurClient root)
{
    /// <summary>One image upload, titled <paramref name="title"/>.</summary>
    public ImgurImageUploadQuery Upload(byte[] imageData, string fileName, string title) =>
        new(root.Options, imageData, fileName, title);
}

/// <summary>The album endpoint of <see cref="ImgurClient"/>.</summary>
public readonly struct ImgurAlbumQueries(ImgurClient root)
{
    /// <summary>An album over the images the delete hashes belong to.</summary>
    public ImgurAlbumCreationQuery Create(IReadOnlyList<string> deleteHashes) => new(root.Options, deleteHashes);
}
