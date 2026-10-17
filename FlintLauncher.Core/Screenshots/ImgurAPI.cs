// SPDX-License-Identifier: GPL-3.0-only
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
//    Skins/SkinUpload.cs) and sends it through the injected NetStack's HttpClient, because
//    NetRequest cannot express multipart Content-Type headers.
//  - The imgur base URL / client id come from FlintConfig (BuildConfig.IMGUR_BASE_URL /
//    IMGUR_CLIENT_ID) by default but are factory-injectable so tests can stub them.

using System.Security;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Screenshots;

/// <summary>Port of the ImgurAPI namespace: the two imgur request factories. An instance
/// rides the injected NetStack (LauncherRuntime.Imgur — same de-singletoning story as the
/// platform clients); the tasks it builds send through that stack's client.</summary>
public sealed class ImgurApi
{
    /// <summary>The network stack this client's uploads flow through.</summary>
    public NetStack Net { get; }

    public ImgurApi(NetStack? net = null)
    {
        Net = net ?? NetStack.Default;
    }

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
        new(shot, baseUrl, clientId, Net);

    /// <summary>Port of ImgurAPI::makeAlbum: group uploaded screenshots into an album
    /// (the payload needs the delete hashes left by the earlier uploads).</summary>
    public ImgurAlbumCreation MakeAlbum(AlbumResult result, IReadOnlyList<ScreenShot> screenshots,
        string? baseUrl = null, string? clientId = null) =>
        new(result, screenshots, baseUrl, clientId, Net);
}

/// <summary>The upload half of ImgurAPI::makeUpload (the Net::Request + RPC::Sink&lt;QString&gt;
/// pair): fills the ScreenShot metadata from the response JSON.</summary>
public sealed class ImgurUpload : FlintTask
{
    private readonly ScreenShot _shot;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private readonly NetStack _net;

    public ImgurUpload(ScreenShot shot, string? baseUrl = null, string? clientId = null,
        NetStack? net = null)
    {
        _shot = shot;
        _baseUrl = baseUrl ?? FlintConfig.Default.ImgurBaseUrl;
        _clientId = clientId ?? FlintConfig.Default.ImgurClientId;
        _net = net ?? NetStack.Default;
    }

    /// <summary>The imgur endpoint: BuildConfig.IMGUR_BASE_URL + "image".</summary>
    public string EndpointUrl => _baseUrl + "image";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        byte[] fileData;
        try
        {
            fileData = await File.ReadAllBytesAsync(_shot.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or SecurityException)
        {
            EmitFailed($"Could not open file {_shot.FilePath} for reading: {e.Message}");
            return;
        }

        var boundary = "flint-" + Guid.NewGuid().ToString("N");
        var body = BuildMultipartBody(boundary, fileData, _shot.FileName(), _shot.BaseName());

        using var message = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        message.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {_clientId}");
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);

        using var response = await _net.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
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
        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            // "imgur server did not reply with JSON"
            EmitFailed("Invalid json reply");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var success = root.ValueKind == JsonValueKind.Object &&
                          root.TryGetProperty("success", out var successValue) &&
                          successValue.ValueKind == JsonValueKind.True;
            if (!success)
            {
                EmitFailed("Screenshot was not uploaded successfully");
                return;
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                _shot.ImgurId = data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? ""
                    : "";
                _shot.Url = data.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.String
                    ? link.GetString() ?? ""
                    : "";
                _shot.ImgurDeleteHash =
                    data.TryGetProperty("deletehash", out var deleteHash) && deleteHash.ValueKind == JsonValueKind.String
                        ? deleteHash.GetString() ?? ""
                        : "";
            }
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
public sealed class ImgurAlbumCreation : FlintTask
{
    private readonly IReadOnlyList<ScreenShot> _screenshots;
    private readonly ImgurApi.AlbumResult _result;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private readonly NetStack _net;

    public ImgurAlbumCreation(ImgurApi.AlbumResult result, IReadOnlyList<ScreenShot> screenshots,
        string? baseUrl = null, string? clientId = null, NetStack? net = null)
    {
        _result = result;
        _screenshots = screenshots;
        _baseUrl = baseUrl ?? FlintConfig.Default.ImgurBaseUrl;
        _clientId = clientId ?? FlintConfig.Default.ImgurClientId;
        _net = net ?? NetStack.Default;
    }

    /// <summary>The imgur endpoint: BuildConfig.IMGUR_BASE_URL + "album".</summary>
    public string EndpointUrl => _baseUrl + "album";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Port of getPayload(): "deletehashes=" + hashes.join("&deletehashes=")
        //                     + "&title=Minecraft%20Screenshots&privacy=hidden"
        var hashes = _screenshots.Select(shot => shot.ImgurDeleteHash).ToArray();
        var data = "deletehashes=" + string.Join("&deletehashes=", hashes) +
                   "&title=Minecraft%20Screenshots&privacy=hidden";

        using var message = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        message.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {_clientId}");
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        // HttpClient emits Content-Type only from the content headers.
        message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");

        using var response = await _net.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
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
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            EmitFailed("Invalid json reply");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var success = root.ValueKind == JsonValueKind.Object &&
                          root.TryGetProperty("success", out var successValue) &&
                          successValue.ValueKind == JsonValueKind.True;
            if (!success)
            {
                EmitFailed("Failed to create album");
                return;
            }

            if (root.TryGetProperty("data", out var albumData) && albumData.ValueKind == JsonValueKind.Object)
            {
                _result.DeleteHash = albumData.TryGetProperty("deletehash", out var deleteHash) &&
                                     deleteHash.ValueKind == JsonValueKind.String
                    ? deleteHash.GetString() ?? ""
                    : "";
                _result.Id = albumData.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? ""
                    : "";
            }
        }

        EmitSucceeded();
    }
}
