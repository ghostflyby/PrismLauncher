// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/screenshots/ImgurAlbumCreation.{h,cpp}: group uploaded screenshots into an
// imgur album by posting the delete hashes as application/x-www-form-urlencoded data.
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Screenshots;

public sealed class ImgurAlbumCreation : FlintTask
{
    private readonly IReadOnlyList<ScreenShot> _screenshots;
    private readonly Result _result;
    private readonly string _baseUrl;
    private readonly string _clientId;

    public ImgurAlbumCreation(Result result, IReadOnlyList<ScreenShot> screenshots, string? baseUrl = null,
        string? clientId = null)
    {
        _result = result;
        _screenshots = screenshots;
        _baseUrl = baseUrl ?? FlintConfig.Default.ImgurBaseUrl;
        _clientId = clientId ?? FlintConfig.Default.ImgurClientId;
    }

    /// <summary>Port of ImgurAlbumCreation::make.</summary>
    public static ImgurAlbumCreation Make(Result result, IReadOnlyList<ScreenShot> screenshots, string? baseUrl = null,
        string? clientId = null) =>
        new(result, screenshots, baseUrl, clientId);

    /// <summary>Port of ImgurAlbumCreation::Result.</summary>
    public sealed record Result
    {
        public string DeleteHash { get; set; } = "";
        public string Id { get; set; } = "";
    }

    /// <summary>The imgur endpoint: BuildConfig.IMGUR_BASE_URL + "album".</summary>
    public string EndpointUrl => _baseUrl + "album";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Port of getReply(): "deletehashes=" + hashes.join("&deletehashes=")
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
            EmitFailed($"HTTP status {statusCode} for {EndpointUrl}");
            return;
        }

        // Port of ImgurAlbumCreation::Sink::finalize.
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
