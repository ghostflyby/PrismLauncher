// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/skins/SkinUpload.{h,cpp}: POST multipart/form-data to
// minecraft/profile/skins with a "file" (image/png) part and a "variant" part.
//
// Deviations:
//  - Upstream builds the request through QHttpMultiPart on QNetworkAccessManager; the port
//    pre-builds an identical multipart body (same part headers, CRLF framing) and sends it
//    with an injected NetStack (defaulting to the canonical process-wide stack). This stays a
//    FlintTask like the C++ Net::Request subclass. The multipart Content-Type must be set on
//    the content headers, which NetRequest cannot do.
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
    private readonly NetStack _net;

    private SkinUpload(string token, byte[] skinData, string variant, AuthEndpointsOptions? auth, NetStack? net)
    {
        _token = token;
        _skinData = skinData;
        _variant = variant;
        _auth = auth ?? new AuthEndpointsOptions();
        _net = net ?? NetStack.Default;
    }

    /// <summary>Port of SkinUpload::make(token, path, variant) — the file is read eagerly.</summary>
    public static SkinUpload Make(string token, AbsolutePath path, string variant,
        AuthEndpointsOptions? auth = null, NetStack? net = null)
    {
        return Make(token, File.ReadAllBytes(path.Value), variant, auth, net);
    }

    /// <summary>Body-bytes variant (FS::read(m_path) upstream).</summary>
    public static SkinUpload Make(string token, byte[] skinData, string variant,
        AuthEndpointsOptions? auth = null, NetStack? net = null)
    {
        return new SkinUpload(token, skinData, variant, auth, net);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var boundary = "flint-" + Guid.NewGuid().ToString("N");
        var body = BuildMultipartBody(boundary, _skinData, _variant);

        EmitStatus("Uploading skin");
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_auth.MinecraftServices.TrimEnd('/') + "/minecraft/profile/skins"));
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);

        using var response = await _net.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
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
