// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/PasteUpload.{h,cpp}: upload a launcher log to a paste site and get back
// a link. Supports the same four paste providers as upstream (0x0.st, hastebin, paste.gg,
// mclo.gs), each with its own request shape and response parsing.
//
// Deviations:
//  - Upstream is a Net::Request subclass posting through QNetworkAccessManager; the port is a
//    FlintTask sending the identical request through an injected NetStack (like
//    Skins/SkinUpload.cs), because NetRequest cannot express multipart/JSON Content-Type headers.
//  - The C++ constructor anonymizes the log in place (logs/AnonymizeLog.cpp); the port applies
//    the same seven rules in AnonymizeLog() before the request is built.
//  - mclo.gs bodies: QUrlQuery percent-encoding is reproduced with Uri.EscapeDataString. Qt
//    leaves a few extra sub-delims unencoded ('!', '(', ')' ...) but the decoded values are
//    identical, and spaces are %20 (never '+') in both.
//  - paste.gg bodies: the typed PasteGgBody record through its source-generated context —
//    the JSON semantics are the third-party contract, the byte shape is not.
//  - The base URL / client id are constructor parameters (like upstream's url argument); tests
//    stub them, there is no static endpoint override.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Net;

/// <summary>The paste.gg create-paste request body (a third-party protocol shape).</summary>
internal sealed record PasteGgBody(string Expires, PasteGgFile[] Files);

internal sealed record PasteGgFile(string Name, PasteGgContent Content);

internal sealed record PasteGgContent(string Format, string Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PasteGgBody))]
internal sealed partial class PasteGgJsonContext : JsonSerializerContext;

public sealed class PasteUpload : FlintTask
{
    public enum PasteType
    {
        // 0x0.st
        NullPointer,

        // hastebin.com
        Hastebin,

        // paste.gg
        PasteGg,

        // mclo.gs
        Mclogs,
    }

    public sealed record PasteTypeInfo(string Name, string DefaultBase, string EndpointPath);

    /// <summary>Port of PasteUpload::PasteTypes (same table, same order).</summary>
    public static readonly PasteTypeInfo[] PasteTypes =
    [
        new("0x0.st", "https://0x0.st", ""),
        new("hastebin", "https://hst.sh", "/documents"),
        new("paste.gg", "https://paste.gg", "/api/v1/pastes"),
        new("mclo.gs", "https://api.mclo.gs", "/1/log"),
    ];

    private readonly string _log;
    private readonly string _baseUrl;
    private readonly PasteType _pasteType;
    private readonly NetStack _net;

    /// <summary>Port of PasteUpload::PasteUpload(log, url, pasteType): anonymizes the log,
    /// falls back to the provider default base when url is empty and resolves the request URL
    /// (with the paste.gg api subdomain HACK kept verbatim). The request rides the injected
    /// NetStack (the runtime's — tests may isolate their own; the default falls back to the
    /// canonical process-wide stack).</summary>
    public PasteUpload(string log, string url, PasteType pasteType, NetStack? net = null)
    {
        _log = AnonymizeLog(log);
        _baseUrl = url;
        _pasteType = pasteType;
        _net = net ?? NetStack.Default;

        var @base = PasteTypes[(int)pasteType];
        if (string.IsNullOrEmpty(_baseUrl))
        {
            _baseUrl = @base.DefaultBase;
        }

        // HACK: Paste's docs say the standard API path is at /api/<version> but the official
        // instance paste.gg doesn't follow that??
        if (pasteType == PasteType.PasteGg && _baseUrl == @base.DefaultBase)
        {
            EndpointUrl = "https://api.paste.gg/v1/pastes";
        }
        else
        {
            EndpointUrl = _baseUrl + @base.EndpointPath;
        }
    }

    /// <summary>Port of pasteLink().</summary>
    public string PasteLink { get; private set; } = "";

    /// <summary>The resolved request URL (m_url): base + provider endpoint path.</summary>
    public string EndpointUrl { get; }

    /// <summary>The anonymized log body this upload sends (exposed for tests).</summary>
    public string Log => _log;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var request = BuildRequest();
        HttpResponseMessage response;
        try
        {
            response = await _net.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            // Port of the Sink::finalize network-error gate (QNetworkReply::error() != NoError).
            EmitFailed($"Network error: {e.Message}");
            return;
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                body = "";
            }

            // Port of Sink::finalize: the status gate happens before the provider switch.
            var reasonPhrase = response.ReasonPhrase ?? "";
            if (statusCode != 200 && statusCode != 201)
            {
                EmitFailed($"Error: {EndpointUrl} returned unexpected status code {statusCode} {reasonPhrase}");
                return;
            }

            switch (_pasteType)
            {
                case PasteType.NullPointer:
                {
                    PasteLink = body.Trim();
                    break;
                }
                case PasteType.Hastebin:
                {
                    if (!TryParseJson(body, out var obj, "hastebin"))
                    {
                        return;
                    }

                    if (obj.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
                    {
                        PasteLink = _baseUrl + "/" + key.GetString();
                    }
                    else
                    {
                        EmitFailed($"Error: {EndpointUrl} returned a malformed response body");
                        return;
                    }

                    break;
                }
                case PasteType.Mclogs:
                {
                    if (!TryParseJson(body, out var obj, "mclogs"))
                    {
                        return;
                    }

                    if (obj.TryGetProperty("success", out var success) &&
                        success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        if (success.ValueKind == JsonValueKind.True)
                        {
                            PasteLink = obj.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                                ? url.GetString() ?? ""
                                : "";
                        }
                        else
                        {
                            var error = obj.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                                ? err.GetString() ?? ""
                                : "";
                            EmitFailed($"Error: {EndpointUrl} returned an error: {error}");
                            return;
                        }
                    }
                    else
                    {
                        EmitFailed($"Error: {EndpointUrl} returned a malformed response body");
                        return;
                    }

                    break;
                }
                case PasteType.PasteGg:
                {
                    if (!TryParseJson(body, out var obj, "pasteGG"))
                    {
                        return;
                    }

                    if (obj.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                    {
                        if (status.GetString() == "success")
                        {
                            var id = "";
                            if (obj.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                                result.TryGetProperty("id", out var resultId) && resultId.ValueKind == JsonValueKind.String)
                            {
                                id = resultId.GetString() ?? "";
                            }

                            PasteLink = _baseUrl + "/p/anonymous/" + id;
                        }
                        else
                        {
                            var error = obj.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                                ? err.GetString() ?? ""
                                : "";
                            var message = obj.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                                ? msg.GetString() ?? ""
                                : "none";
                            EmitFailed($"Error: {EndpointUrl} returned an error code: {error}\nError message: {message}");
                            return;
                        }
                    }
                    else
                    {
                        EmitFailed($"Error: {EndpointUrl} returned a malformed response body");
                        return;
                    }

                    break;
                }
            }

            EmitSucceeded();
        }
    }

    private bool TryParseJson(string body, out JsonElement obj, string provider)
    {
        obj = default;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            EmitFailed(
                $"Failed to parse response from {provider} server: expected JSON but got an invalid response. Error: {e.Message}");
            return false;
        }

        obj = doc.RootElement.Clone();
        if (obj.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        EmitFailed($"Error: {EndpointUrl} returned a malformed response body");
        return false;
    }

    /// <summary>Builds the exact request the C++ getReply() produces for the configured type.</summary>
    internal HttpRequestMessage BuildRequest()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        switch (_pasteType)
        {
            case PasteType.NullPointer:
            {
                // QHttpMultiPart{FormDataType} with a single part: Content-Type set first, then
                // the Content-Disposition.
                var boundary = "flint-" + Guid.NewGuid().ToString("N");
                var body = BuildNullPointerMultipart(boundary, _log);
                message.Content = new ByteArrayContent(body);
                message.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
                break;
            }
            case PasteType.Hastebin:
            {
                // m_network->post(request, m_log.toUtf8()) — no Content-Type header set.
                message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(_log));
                break;
            }
            case PasteType.Mclogs:
            {
                // QUrlQuery postData; postData.addQueryItem("content", m_log)
                // HttpClient only emits Content-Type from the content headers; a request-level
                // one is silently dropped.
                message.Content = new ByteArrayContent(
                    Encoding.UTF8.GetBytes("content=" + Uri.EscapeDataString(_log)));
                message.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
                break;
            }
            case PasteType.PasteGg:
            {
                message.Content = new ByteArrayContent(BuildPasteGgBody(_log));
                message.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                break;
            }
        }

        return message;
    }

    /// <summary>The exact QHttpMultiPart of the NullPointer case: one "file" part with
    /// Content-Type: text/plain and filename="log.txt".</summary>
    public static byte[] BuildNullPointerMultipart(string boundary, string log)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Type: text/plain\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("Content-Disposition: form-data; name=\"file\"; filename=\"log.txt\"\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.UTF8.GetBytes(log));
        stream.Write(Encoding.Latin1.GetBytes("\r\n"));
        stream.Write(Encoding.Latin1.GetBytes("--" + boundary + "--\r\n"));
        return stream.ToArray();
    }

    /// <summary>The paste.gg JSON document: {"expires": &lt;utc+100d ISO&gt;, "files":
    /// [{"name": "log.txt", "content": {"format": "text", "value": &lt;log&gt;}}]}</summary>
    public static byte[] BuildPasteGgBody(string log)
    {
        var expires = DateTime.UtcNow.AddDays(100).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToUtf8Bytes(
            new PasteGgBody(expires, [new PasteGgFile("log.txt", new PasteGgContent("text", log))]),
            PasteGgJsonContext.Default.PasteGgBody);
    }

    // ------------------------------------------------------------- AnonymizeLog.cpp

    private static (Regex Reg, string With)[]? _anonymizeRules;

    /// <summary>Port of anonymizeLog(QString&): applies the seven redaction rules (all
    /// case-insensitive, all occurrences) and returns the anonymized copy.</summary>
    public static string AnonymizeLog(string log)
    {
        _anonymizeRules ??=
        [
            (new Regex(@"C:\\Users\\([^\\]+)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "C:\\Users\\********\\"), // windows
            (new Regex(@"C:/Users/([^/]+)/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "C:/Users/********/"), // windows with forward slashes
            (new Regex(@"(?<!\w)/home/[^/]+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "/home/********/"), // linux
            (new Regex(@"(?<!\w)/Users/[^/]+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "/Users/********/"), // macos
            (new Regex(@"\(Session ID is [^)]+\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "(Session ID is <SESSION_TOKEN>)"), // SESSION_TOKEN
            (new Regex(@"new refresh token: ""[^""]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "new refresh token: \"<TOKEN>\""), // refresh token
            (new Regex(@"""device_code"" :  ""[^""]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "\"device_code\" :  \"<DEVICE_CODE>\""), // device code
        ];
        foreach (var (reg, with) in _anonymizeRules)
        {
            log = reg.Replace(log, with);
        }

        return log;
    }
}
