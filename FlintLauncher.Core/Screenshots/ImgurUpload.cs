// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/screenshots/ImgurUpload.{h,cpp}: POST one screenshot to imgur as
// multipart/form-data and parse the JSON response into the ScreenShot metadata.
//
// Deviations:
//  - Upstream builds the request through QHttpMultiPart on QNetworkAccessManager (a
//    Net::Request subclass); the port pre-builds an identical multipart body (same part
//    headers and CRLF framing, like Skins/SkinUpload.cs) and sends it with the shared
//    Http.Client, because NetRequest cannot express multipart Content-Type headers.
//  - The imgur base URL / client id come from FlintConfig (BuildConfig.IMGUR_BASE_URL /
//    IMGUR_CLIENT_ID) by default but are constructor-injectable so tests can stub them.
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Screenshots;

public sealed class ImgurUpload : FlintTask
{
    private readonly ScreenShot _shot;
    private readonly string _baseUrl;
    private readonly string _clientId;

    public ImgurUpload(ScreenShot shot, string? baseUrl = null, string? clientId = null)
    {
        _shot = shot;
        _baseUrl = baseUrl ?? FlintConfig.Default.ImgurBaseUrl;
        _clientId = clientId ?? FlintConfig.Default.ImgurClientId;
    }

    /// <summary>Port of ImgurUpload::make.</summary>
    public static ImgurUpload Make(ScreenShot shot, string? baseUrl = null, string? clientId = null) =>
        new(shot, baseUrl, clientId);

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
                                  or ArgumentException or System.Security.SecurityException)
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

        using var response = await Http.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
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

        // Port of ImgurUpload::Sink::finalize.
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
    /// The exact QHttpMultiPart(QHttpMultiPart::FormDataType) framing of ImgurUpload::getReply:
    /// the image part carries "Content-Type: image/png" then the Content-Disposition with the
    /// file name, followed by the "type" = "file" part and the "title" = baseName part.
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
