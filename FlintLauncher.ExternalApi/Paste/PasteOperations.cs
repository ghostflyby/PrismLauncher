// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Lenny McLennington <lenny@sneed.church>
// SPDX-FileCopyrightText: 2022 Swirl <swurl@swurl.xyz>
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
// The request and reply halves of launcher/net/PasteUpload: each provider's request shape
// (getReply()) and response parsing (Sink::finalize). Notes:
//  - mclo.gs bodies: the urlencoded form (getPayload's QUrlQuery "content" field) is
//    delegated to FormUrlEncodedContent — its encoding is the BCL's, not this port's.
//  - paste.gg bodies: the typed PasteGgBody record through its source-generated context —
//    the JSON semantics are the third-party contract, the byte shape is not.
//  - the reply failures carry the upstream texts as InvalidOperationException inside the
//    wrapped failure.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi.Paste;

/// <summary>POST the content to the configured provider; the result is the link the
/// provider handed back. A provider error, a malformed reply or a reply without a link
/// fails.</summary>
public readonly record struct PasteUploadQuery(PasteClientOptions Options, string Content) : IApiOperation<string>
{
    private PasteTypeInfo Info => PasteClient.Types[(int)Options.Type];

    /// <summary>The base the provider's links are built on: the configured base, or the
    /// provider default when it is empty.</summary>
    public string BaseUrl => string.IsNullOrEmpty(Options.BaseUrl) ? Info.DefaultBase : Options.BaseUrl;

    /// <summary>The request URL: the base plus the provider endpoint path.</summary>
    public string Url =>
        // HACK: Paste's docs say the standard API path is at /api/<version> but the official
        // instance paste.gg doesn't follow that??
        Options.Type == PasteType.PasteGg && BaseUrl == Info.DefaultBase
            ? "https://api.paste.gg/v1/pastes"
            : BaseUrl + Info.EndpointPath;

    /// <summary>Builds the request the provider's flow calls for.</summary>
    public HttpRequestMessage CreateRequest()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Url);
        switch (Options.Type)
        {
            case PasteType.NullPointer:
            {
                var content = new MultipartFormDataContent("flint-" + Guid.NewGuid().ToString("N"));
                content.Add(new StringContent(Content), "file", "log.txt");
                message.Content = content;
                break;
            }
            case PasteType.Hastebin:
            {
                message.Content = new StringContent(Content);
                break;
            }
            case PasteType.Mclogs:
            {
                message.Content = new FormUrlEncodedContent(
                    [new KeyValuePair<string, string>("content", Content)]);
                break;
            }
            case PasteType.PasteGg:
            {
                message.Content = new ByteArrayContent(PasteBodies.PasteGg(Content));
                message.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                break;
            }
        }

        return message;
    }

    /// <summary>A transport failure carries the Sink::finalize network-error text
    /// (QNetworkReply::error() != NoError); the reply rules' failures keep their own.</summary>
    public Exception WrapFailure(Exception failure) => failure is HttpRequestException network
        ? new PasteApiException($"Network error: {network.Message}", network)
        : new PasteApiException(failure.Message, failure);

    public async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            body = "";
        }

        // Sink::finalize: the status gate happens before the provider switch.
        var statusCode = (int)response.StatusCode;
        if (statusCode != 200 && statusCode != 201)
        {
            throw new InvalidOperationException(
                $"Error: {Url} returned unexpected status code {statusCode} {response.ReasonPhrase ?? ""}");
        }

        var link = Options.Type switch
        {
            PasteType.NullPointer => body.Trim(),
            PasteType.Hastebin => ReadHastebin(body),
            PasteType.Mclogs => ReadMclogs(body),
            PasteType.PasteGg => ReadPasteGg(body),
            _ => "",
        };

        if (link.Length == 0)
        {
            throw new InvalidOperationException("the paste server returned no link");
        }

        return link;
    }

    private string ReadHastebin(string body)
    {
        var reply = BindReply(body, PasteGgJsonContext.Default.HastebinReply, "hastebin");
        return reply?.Key is { } key
            ? BaseUrl + "/" + key
            : throw MalformedReply();
    }

    private string ReadMclogs(string body)
    {
        var reply = BindReply(body, PasteGgJsonContext.Default.MclogsReply, "mclogs");
        return reply?.Success switch
        {
            true => reply.Url ?? "",
            false => throw new InvalidOperationException($"Error: {Url} returned an error: {reply.Error ?? ""}"),
            _ => throw MalformedReply(),
        };
    }

    private string ReadPasteGg(string body)
    {
        var reply = BindReply(body, PasteGgJsonContext.Default.PasteGgReply, "pasteGG");
        return reply?.Status switch
        {
            "success" => BaseUrl + "/p/anonymous/" + (reply.Result?.Id ?? ""),
            "error" => throw new InvalidOperationException(
                $"Error: {Url} returned an error code: {reply.Error ?? ""}\nError message: {reply.Message ?? "none"}"),
            _ => throw MalformedReply(),
        };
    }

    private InvalidOperationException MalformedReply() =>
        new($"Error: {Url} returned a malformed response body");

    /// <summary>A reply document; a body that does not bind fails with the provider's
    /// parse-failure text.</summary>
    private static T? BindReply<T>(string body, JsonTypeInfo<T> typeInfo, string provider)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"Failed to parse response from {provider} server: expected JSON but got an invalid response. Error: {e.Message}",
                e);
        }
    }
}

/// <summary>The paste.gg JSON document: {"expires": &lt;utc+100d ISO&gt;, "files":
/// [{"name": "log.txt", "content": {"format": "text", "value": &lt;log&gt;}}]}</summary>
internal static class PasteBodies
{
    internal static byte[] PasteGg(string log)
    {
        var expires = DateTime.UtcNow.AddDays(100).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToUtf8Bytes(
            new PasteGgBody(expires, [new PasteGgFile("log.txt", new PasteGgContent("text", log))]),
            PasteGgJsonContext.Default.PasteGgBody);
    }
}

/// <summary>The paste endpoint of <see cref="PasteClient"/>.</summary>
public readonly struct PasteQueries(PasteClient root)
{
    /// <summary>One upload of <paramref name="content"/> to the configured provider.</summary>
    public PasteUploadQuery Upload(string content) => new(root.Options, content);
}
