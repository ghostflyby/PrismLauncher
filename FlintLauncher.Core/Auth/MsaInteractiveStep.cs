// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
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
// Port of launcher/minecraft/auth/steps/MSAStep.{h,cpp}: the interactive Microsoft
// authorization-code flow and the silent refresh-token flow.
//
// Deviations (Katabasis/QOAuth2AuthorizationCodeFlow is replaced by a plain implementation):
//  - The authorize URL is built the way QOAuth2AuthorizationCodeFlow::buildAuthenticateUrl does
//    (response_type=code, client_id, scope, redirect_uri plus the upstream
//    modifyParametersFunction addition prompt=select_account) and the code is exchanged at the
//    token endpoint with grant_type=authorization_code (client_id, code, redirect_uri).
//  - The QOAuthHttpServerReplyHandler loopback listener is re-implemented on a TcpListener bound
//    to 127.0.0.1 (ephemeral port), redirect_uri = http://127.0.0.1:<port>/, serving the same
//    "Login Successful, redirecting..." HTML.
//  - The CustomOAuthOobReplyHandler custom-scheme fallback is not ported (scheme-handler
//    registration is desktop-shell specific); the launcher shell can feed a custom-scheme
//    redirect (e.g. flint://oauth/microsoft?code=...) through CompleteWithRedirectUri(), which
//    mirrors the QOAuthOobReplyHandler::callbackReceived path.
//  - granted() -> the MSA token fields are filled from the token response (issueInstant = now,
//    notAfter = now + expires_in, extra = the whole response map, refresh_token, token);
//    error(QString, QString, uri) -> STATE_FAILED_HARD with the description;
//    requestFailed(Error) -> STATE_OFFLINE for network errors on a granted/silent session,
//    otherwise STATE_FAILED_SOFT ("Failed to refresh token." when silent).

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Auth;

public sealed class MsaInteractiveStep(AccountData data, bool silent = false, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();
    private string _clientId = "";
    private LoopbackListener? _listener;

    /// <summary>Port of the authorizeWithBrowser(const QUrl&amp;) signal.</summary>
    public event Action<string>? AuthorizeWithBrowser;

    public override string Describe() => "Logging in with Microsoft account.";

    public override void Abort()
    {
        _listener?.Dispose();
        _listener = null;
    }

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        _clientId = _auth.MsaClientId;
        if (silent)
        {
            if (Data.MsaClientId != _clientId)
            {
                EmitFinished(AccountTaskState.Disabled,
                    "Microsoft user authentication failed - client identification has changed.");
                return;
            }

            if (Data.MsaToken.RefreshToken.Length == 0)
            {
                EmitFinished(AccountTaskState.Disabled,
                    "Microsoft user authentication failed - refresh token is empty.");
                return;
            }

            await RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // *m_data = AccountData(); m_data->msaClientID = m_clientId;
        Data.Type = AccountType.Msa;
        Data.MsaClientId = _clientId;
        Data.MsaToken = new Token();
        Data.UserToken = new Token();
        Data.MojangServicesToken = new Token();
        Data.YggdrasilToken = new Token();
        Data.MinecraftProfile = new MinecraftProfile();
        Data.MinecraftEntitlement = new MinecraftEntitlement();
        Data.Validity = Validity.None;
        Data.ErrorString = "";
        Data.NetworkError = null;
        Data.AccountState = AccountState.Unchecked;

        await GrantAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ refresh flow

    private async Task RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var payload = FormEncode(
            ("grant_type", "refresh_token"),
            ("client_id", _clientId),
            ("refresh_token", Data.MsaToken.RefreshToken));
        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(_auth.MsaToken),
            Body = payload,
            Headers = FormHeaders,
        }, _auth.Http, cancellationToken).ConfigureAwait(false);

        var rsp = ParseTokenResponse(response.Body);
        if (rsp.Error.Length != 0)
        {
            // QOAuth2AuthorizationCodeFlow::error(QString, QString, uri) path
            EmitFinished(AccountTaskState.FailedHard, rsp.ErrorDescription.Length != 0 ? rsp.ErrorDescription : rsp.Error);
            return;
        }

        if (!response.Success)
        {
            // QOAuth2AuthorizationCodeFlow::requestFailed(Error) path: silent + granted means
            // network errors are offline states, everything else is a soft failure.
            var state = response.Exception is HttpRequestException or IOException
                ? AccountTaskState.Offline
                : AccountTaskState.FailedSoft;
            EmitFinished(state, "Failed to refresh token.");
            return;
        }

        ApplyGrantedTokens(rsp);
        EmitFinished(AccountTaskState.Working, "Got MSA token");
    }

    // ------------------------------------------------------------- interactive flow

    private async Task GrantAsync(CancellationToken cancellationToken)
    {
        _listener = new LoopbackListener();
        var redirectUri = _listener.Start();

        // QOAuth2AuthorizationCodeFlow::buildAuthenticateUrl() + modifyParametersFunction(prompt)
        var authorizeUrl = _auth.MsaAuthorize +
                           "?response_type=code" +
                           "&client_id=" + Uri.EscapeDataString(_clientId) +
                           "&scope=" + Uri.EscapeDataString("XboxLive.SignIn XboxLive.offline_access") +
                           "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                           "&prompt=select_account";

        AuthorizeWithBrowser?.Invoke(authorizeUrl);

        string? code = null;
        string? oauthError = null;
        try
        {
            var redirect = await _listener.WaitForRedirectAsync(cancellationToken).ConfigureAwait(false);
            if (redirect is not null)
            {
                var query = ParseQuery(redirect.Query);
                if (query.TryGetValue("error", out var errorValue))
                {
                    oauthError = query.TryGetValue("error_description", out var description) && description.Length != 0
                        ? description
                        : errorValue;
                }
                else if (query.TryGetValue("code", out var codeValue))
                {
                    code = codeValue;
                }
            }
        }
        finally
        {
            _listener.Dispose();
            _listener = null;
        }

        if (oauthError is not null)
        {
            // the error(QString, QString, uri) signal path
            EmitFinished(AccountTaskState.FailedHard, oauthError);
            return;
        }

        if (code is null)
        {
            EmitFinished(AccountTaskState.FailedHard, "Microsoft user authentication failed.");
            return;
        }

        var payload = FormEncode(
            ("grant_type", "authorization_code"),
            ("client_id", _clientId),
            ("code", code),
            ("redirect_uri", redirectUri));
        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(_auth.MsaToken),
            Body = payload,
            Headers = FormHeaders,
        }, _auth.Http, cancellationToken).ConfigureAwait(false);

        var rsp = ParseTokenResponse(response.Body);
        if (rsp.Error.Length != 0)
        {
            EmitFinished(AccountTaskState.FailedHard, rsp.ErrorDescription.Length != 0 ? rsp.ErrorDescription : rsp.Error);
            return;
        }

        if (!response.Success)
        {
            EmitFinished(AccountTaskState.FailedHard, "Microsoft user authentication failed.");
            return;
        }

        ApplyGrantedTokens(rsp);
        EmitFinished(AccountTaskState.Working, "Got MSA token");
    }

    /// <summary>
    /// Port of the CustomOAuthOobReplyHandler path: lets the app shell feed a custom-scheme
    /// redirect ( prismlauncher://oauth/microsoft?code=... style) in place of the loopback
    /// listener. Returns false when no listener is waiting (i.e. the interactive flow is not
    /// running) — the shell should then start a fresh interactive login.
    /// </summary>
    public bool CompleteWithRedirectUri(string uri)
    {
        if (_listener is not { } httpListener)
        {
            return false;
        }

        httpListener.CompleteExternally(new Uri(uri));
        return true;
    }

    // ----------------------------------------------------------------- shared pieces

    private void ApplyGrantedTokens(TokenResponse rsp)
    {
        Data.MsaClientId = _clientId;
        var now = DateTimeOffset.UtcNow;
        Data.MsaToken.IssueInstant = now;
        Data.MsaToken.NotAfter = now.AddSeconds(rsp.ExpiresIn);
        Data.MsaToken.Extra = rsp.Extra;
        Data.MsaToken.RefreshToken = rsp.RefreshToken;
        Data.MsaToken.Value = rsp.AccessToken;
    }

    private static readonly KeyValuePair<string, string>[] FormHeaders =
    [
        new("Content-Type", "application/x-www-form-urlencoded"),
        new("Accept", "application/json"),
    ];

    private static string FormEncode(params (string key, string value)[] pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return sb.ToString();
    }

    private sealed record TokenResponse(
        string AccessToken,
        string RefreshToken,
        int ExpiresIn,
        string Error,
        string ErrorDescription,
        Dictionary<string, JsonElement> Extra);

    private static TokenResponse ParseTokenResponse(byte[] data)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(data) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            obj = null;
        }

        if (obj is null)
        {
            return new TokenResponse("", "", 0, "", "", new Dictionary<string, JsonElement>());
        }

        var extra = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in obj)
        {
            if (value is not null)
            {
                extra[key] = AccountData.ToElement(value);
            }
        }

        return new TokenResponse(
            AccountData.GetString(AccountData.Get(obj, "access_token")),
            AccountData.GetString(AccountData.Get(obj, "refresh_token")),
            AccountData.EnsureInteger(obj, "expires_in"),
            AccountData.GetString(AccountData.Get(obj, "error")),
            AccountData.GetString(AccountData.Get(obj, "error_description")),
            extra);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    /// <summary>Minimal HTTP listener standing in for QOAuthHttpServerReplyHandler.</summary>
    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private readonly TaskCompletionSource<Uri?> _redirectSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly CancellationTokenSource _acceptCts = new();
        private bool _disposed;

        public string Start()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(_acceptCts.Token);
            return $"http://127.0.0.1:{port}/";
        }

        public async Task<Uri?> WaitForRedirectAsync(CancellationToken cancellationToken)
        {
            var registration = cancellationToken.Register(() =>
                _redirectSource.TrySetCanceled(cancellationToken));
            try
            {
                return await _redirectSource.Task.ConfigureAwait(false);
            }
            finally
            {
                await registration.DisposeAsync();
            }
        }

        /// <summary>Feeds a callback URI from outside (custom-scheme handler).</summary>
        public void CompleteExternally(Uri uri)
        {
            _redirectSource.TrySetResult(uri);
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }

                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                await using var stream = client.GetStream();
                var buffer = new byte[8192];
                var received = 0;
                string requestLine = "";
                while (received < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(received, buffer.Length - received)).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    var text = Encoding.Latin1.GetString(buffer, 0, received);
                    var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                    if (lineEnd >= 0)
                    {
                        requestLine = text[..lineEnd];
                        break;
                    }
                }

                // "GET /?code=... HTTP/1.1"
                Uri? requestUri = null;
                var parts = requestLine.Split(' ');
                if (parts.Length >= 2 && Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var uri))
                {
                    requestUri = uri;
                }

                var page = "\n    <noscript>\n      <meta http-equiv=\"Refresh\" content=\"0; URL=%1\" />\n    </noscript>\n" +
                           "    Login Successful, redirecting...\n    <script>\n      window.location.replace(\"%1\");\n    </script>\n    ";
                page = page.Replace("%1", "https://prismlauncher.org/successful-login");
                var body = Encoding.UTF8.GetBytes(page);
                var header = Encoding.Latin1.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header).ConfigureAwait(false);
                await stream.WriteAsync(body).ConfigureAwait(false);

                if (requestUri is not null)
                {
                    _redirectSource.TrySetResult(requestUri);
                }
            }
            catch (Exception)
            {
                // connection errors are irrelevant; the browser retries if needed
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _acceptCts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // best-effort stop: the loopback listener dies with the process anyway
            }

            _redirectSource.TrySetResult(null);
        }
    }
}
