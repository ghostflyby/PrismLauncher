// SPDX-License-Identifier: GPL-3.0-only
// In-process HttpListener stub for the auth/skin endpoints (same pattern as NetStackTests'
// TestHttpServer). Every request is recorded (method, path, query, headers, body) and the
// endpoint behaviour is driven by the knobs, so the whole MSA flow can be exercised without
// touching the real services. Used by AuthFlowTests and SkinApiTests, which point their own
// AuthEndpointsOptions instances at it.
using System.Net;
using System.Text;

namespace Flint.Tests;

internal sealed class AuthStubServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public AuthStubServer()
    {
        // TestPorts owns the port lease AND the rebind-retry (HttpListener cannot adopt the
        // lease socket, so losing the takeover window retries on a fresh port)
        (_listener, BaseUrl) = PortAllocator.StartHttpListener();
        _ = Task.Run(ListenLoop);
    }

    public string BaseUrl { get; }

    public List<RecordedRequest> Requests { get; } = [];

    // ---- behaviour knobs ----
    public enum DeviceCodeTokenMode
    {
        PendingThenSuccess,
        AlwaysExpired,
    }

    /// <summary>How many /token calls answer authorization_pending before the success response.</summary>
    public int PendingTokenResponses { get; set; } = 2;

    public DeviceCodeTokenMode TokenMode { get; set; } = DeviceCodeTokenMode.PendingThenSuccess;

    /// <summary>xsts/authorize answers 401 with this XErr code instead of a token (null = success).</summary>
    public long? XErr { get; set; }

    /// <summary>HTTP status of GET /minecraft/profile (200, 404, 500, ...).</summary>
    public int ProfileStatus { get; set; } = 200;

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>The last recorded request for a method+path, or null.</summary>
    public RecordedRequest? Request(string method, string path)
    {
        lock (Requests)
        {
            return Requests.LastOrDefault(r => r.Method == method && r.Path == path);
        }
    }

    public string ProfileJson => $$"""
        {
            "id": "b50ad385829d3141a2167e7d7539ba7f",
            "name": "TestPlayer",
            "skins": [
                {
                    "id": "skin-id",
                    "state": "ACTIVE",
                    "url": "{{BaseUrl}}/skin.png",
                    "variant": "CLASSIC"
                }
            ],
            "capes": [
                {
                    "id": "cape-id",
                    "state": "ACTIVE",
                    "url": "{{BaseUrl}}/cape.png",
                    "alias": "Migrator"
                }
            ]
        }
        """;

    private const string XTokenJson = """
        {
            "IssueInstant": "2020-12-07T19:52:08.4463796Z",
            "NotAfter": "2020-12-21T19:52:08.4463796Z",
            "Token": "USER_TOKEN_STUB",
            "DisplayClaims": {
                "xui": [
                    { "uhs": "UHS_STUB" }
                ]
            }
        }
        """;

    private const string XstsJson = """
        {
            "IssueInstant": "2020-12-07T19:52:08.4463796Z",
            "NotAfter": "2020-12-21T19:52:08.4463796Z",
            "Token": "XSTS_TOKEN_STUB",
            "DisplayClaims": {
                "xui": [
                    { "uhs": "UHS_STUB", "xid": "2533274837352155" }
                ]
            }
        }
        """;

    private string XstsErrorJson(long xerr) => $$"""
        {
            "Identity": "0",
            "XErr": {{xerr}},
            "Message": "",
            "Redirect": "https://start.ui.xboxlive.com/AddChildToFamily"
        }
        """;

    private async Task ListenLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break;
            }
            try
            {
                Handle(context);
            }
            catch (Exception)
            {
                // never let a stub bug crash the listener loop
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        var recorded = new RecordedRequest(
            ctx.Request.HttpMethod,
            ctx.Request.Url!.AbsolutePath,
            ctx.Request.Url!.Query,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            body);
        foreach (var key in ctx.Request.Headers.AllKeys)
        {
            if (key is not null)
            {
                recorded.Headers[key] = ctx.Request.Headers[key] ?? "";
            }
        }
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        var path = recorded.Path;
        var response = (path, recorded.Method) switch
        {
            ("/devicecode", "POST") => (200, """
                {
                    "device_code": "DEV_CODE_STUB",
                    "user_code": "FLINT-CODE",
                    "verification_uri": "https://microsoft.com/link",
                    "expires_in": 900,
                    "interval": 1
                }
                """),
            ("/token", "POST") => TokenResponse(),
            ("/user/authenticate", "POST") => (200, XTokenJson),
            ("/xsts/authorize", "POST") => XErr is { } xerr ? (401, XstsErrorJson(xerr)) : (200, XstsJson),
            ("/launcher/login", "POST") => (200, """
                {
                    "expires_in": 86400,
                    "username": "d71da8eef9b34d24a1f7d2a4bb2b1b7c",
                    "access_token": "MC_ACCESS_TOKEN_STUB",
                    "token_type": "Bearer"
                }
                """),
            ("/entitlements/license", "GET") => (200, """
                {
                    "items": [
                        { "name": "game_minecraft" },
                        { "name": "product_minecraft" }
                    ],
                    "signature": "sig",
                    "jwt": "jwt"
                }
                """),
            ("/minecraft/profile", "GET") => (ProfileStatus, ProfileStatus == 200 ? ProfileJson : ""),
            ("/skin.png", "GET") => (200, "fake-png-bytes-for-testing"),
            ("/cape.png", "GET") => (200, "fake-cape-bytes"),
            // skins API
            ("/minecraft/profile/skins", "POST") => (200, ""),
            ("/minecraft/profile/skins/active", "DELETE") => (200, ""),
            ("/minecraft/profile/capes/active", "PUT") => (200, ""),
            ("/minecraft/profile/capes/active", "DELETE") => (200, ""),
            _ => (404, "{}"),
        };

        ctx.Response.StatusCode = response.Item1;
        var bytes = Encoding.UTF8.GetBytes(response.Item2);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
    }

    private (int, string) TokenResponse()
    {
        if (TokenMode == DeviceCodeTokenMode.AlwaysExpired)
        {
            return (200, """
                {
                    "error": "expired_token",
                    "error_description": "Device token has expired."
                }
                """);
        }
        if (PendingTokenResponses > 0)
        {
            PendingTokenResponses--;
            return (200, """
                {
                    "error": "authorization_pending",
                    "error_description": "Authorization pending."
                }
                """);
        }
        return (200, """
            {
                "access_token": "MSA_ACCESS",
                "refresh_token": "MSA_REFRESH",
                "token_type": "Bearer",
                "expires_in": 86400
            }
            """);
    }
}

internal sealed record RecordedRequest(
    string Method,
    string Path,
    string Query,
    Dictionary<string, string> Headers,
    string Body);
