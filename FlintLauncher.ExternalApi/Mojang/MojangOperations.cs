// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
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
// The requests of launcher/minecraft/auth/steps (MSADeviceCodeStep, MSAStep, XboxUserStep,
// XboxAuthorizationStep, LauncherLoginStep, EntitlementsStep, MinecraftProfileStep,
// GetSkinStep) and launcher/minecraft/skins (SkinUpload, CapeChange, SkinDelete). The JSON
// bodies are typed request records through the contexts; the form bodies are
// FormUrlEncodedContent's. Each endpoint's read keeps the status rule of its upstream
// step.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi.Mojang;

/// <summary>The request shapes and read patterns the account operations call.</summary>
internal static class MojangRequests
{
    /// <summary>The Xbox Live scopes the Microsoft grants request.</summary>
    internal const string XboxLiveScopes = "XboxLive.SignIn XboxLive.offline_access";

    /// <summary>A form POST: application/x-www-form-urlencoded, answered as JSON.</summary>
    internal static HttpRequestMessage Form(string url, params (string Key, string Value)[] pairs)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(pairs.Select(
                pair => new KeyValuePair<string, string>(pair.Key, pair.Value))),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    /// <summary>A JSON POST answered as JSON: the typed request body through its context;
    /// the Xbox Live endpoints also carry the contract version (it prevents a 400
    /// bad-request).</summary>
    internal static HttpRequestMessage Json<T>(string url, T body, JsonTypeInfo<T> typeInfo,
        bool xboxContract)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, typeInfo),
                new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (xboxContract)
        {
            // https://learn.microsoft.com/en-us/gaming/gdk/_content/gc/reference/live/rest/additional/httpstandardheaders
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        }

        return request;
    }

    /// <summary>A bearer-authorized GET answered as JSON.</summary>
    internal static HttpRequestMessage BearerGet(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        return request;
    }

    /// <summary>The texture download's binary read; a body that cannot be read reads as
    /// empty, cancellation propagates. The JSON endpoints deserialize their stream through
    /// MojangJson instead.</summary>
    internal static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>The pattern for the sign-in endpoints: a non-2xx answer fails with its status
    /// and the request URL. The text reaches the user, so it carries no body excerpt.</summary>
    internal static void EnsureSuccess(HttpResponseMessage response, Uri url)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP status {(int)response.StatusCode} for {url}",
                inner: null, statusCode: response.StatusCode);
        }
    }

    internal static string Services(MojangClientOptions options, string path) =>
        options.MinecraftServices.TrimEnd('/') + path;

    /// <summary>The grant of an error-free token answer: a 2xx without the grant members is
    /// a malformed answer, not an empty grant.</summary>
    internal static MojangTokenGranted Granted(MojangTokenDocument document) => new(
        document.AccessToken ?? throw new JsonException("'access_token' is not a string"),
        document.RefreshToken,
        document.ExpiresIn ?? throw new JsonException("'expires_in' is not a number"),
        document.Extra);
}

// ---------------------------------------------------------------------------- microsoft

/// <summary>POST the device authorization start (RFC 8628): the device and user codes, the
/// verification URI, the expiry and the polling interval.</summary>
public readonly record struct MojangDeviceAuthorizationQuery(MojangClientOptions Options, string ClientId)
    : IApiOperation<MojangDeviceAuthorization>
{
    public HttpRequestMessage CreateRequest() =>
        MojangRequests.Form(Options.MsaDeviceCode, ("client_id", ClientId), ("scope", MojangRequests.XboxLiveScopes));

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangDeviceAuthorization> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        MojangRequests.EnsureSuccess(response, new Uri(Options.MsaDeviceCode));
        return await MojangJson.LoadDeviceAuthorizationAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}

/// <summary>POST one device-code token poll: the grant, or the refusal that drives the
/// polling (pending / slow_down / failure).</summary>
public readonly record struct MojangDeviceTokenQuery(MojangClientOptions Options, string ClientId, string DeviceCode)
    : IApiOperation<MojangTokenAnswer>
{
    public HttpRequestMessage CreateRequest() =>
        MojangRequests.Form(Options.MsaToken,
            ("client_id", ClientId),
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("device_code", DeviceCode));

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangTokenAnswer> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var document = await MojangJson.LoadTokenDocumentAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (document.Error is not null || document.ErrorDescription is not null)
        {
            return new MojangTokenRefused(document.Error ?? "", document.ErrorDescription ?? "");
        }

        MojangRequests.EnsureSuccess(response, new Uri(Options.MsaToken));
        return MojangRequests.Granted(document);
    }
}

/// <summary>POST the refresh-token grant: the grant, or the OAuth refusal.</summary>
public readonly record struct MojangRefreshTokenQuery(MojangClientOptions Options, string ClientId, string RefreshToken)
    : IApiOperation<MojangTokenAnswer>
{
    public HttpRequestMessage CreateRequest() =>
        MojangRequests.Form(Options.MsaToken,
            ("grant_type", "refresh_token"),
            ("client_id", ClientId),
            ("refresh_token", RefreshToken));

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangTokenAnswer> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var document = await MojangJson.LoadTokenDocumentAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (document.Error is not null)
        {
            return new MojangTokenRefused(document.Error, document.ErrorDescription ?? "");
        }

        MojangRequests.EnsureSuccess(response, new Uri(Options.MsaToken));
        return MojangRequests.Granted(document);
    }
}

/// <summary>POST the authorization-code grant of the interactive flow: the grant, or the
/// OAuth refusal.</summary>
public readonly record struct MojangAuthorizationCodeQuery(
    MojangClientOptions Options,
    string ClientId,
    string Code,
    string RedirectUri) : IApiOperation<MojangTokenAnswer>
{
    public HttpRequestMessage CreateRequest() =>
        MojangRequests.Form(Options.MsaToken,
            ("grant_type", "authorization_code"),
            ("client_id", ClientId),
            ("code", Code),
            ("redirect_uri", RedirectUri));

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangTokenAnswer> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var document = await MojangJson.LoadTokenDocumentAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (document.Error is not null)
        {
            return new MojangTokenRefused(document.Error, document.ErrorDescription ?? "");
        }

        MojangRequests.EnsureSuccess(response, new Uri(Options.MsaToken));
        return MojangRequests.Granted(document);
    }
}

// --------------------------------------------------------------------------------- xbox
/// <summary>POST the Xbox Live user authentication with the MSA access token as the RPS
/// ticket.</summary>
public readonly record struct MojangXboxUserQuery(MojangClientOptions Options, string MsaAccessToken)
    : IApiOperation<MojangXboxToken>
{
    public HttpRequestMessage CreateRequest() => MojangRequests.Json(Options.XboxUserAuth,
        new MojangXboxUserAuthRequest(
            new MojangRpsPropertiesRequest("RPS", "user.auth.xboxlive.com", "d=" + MsaAccessToken),
            "http://auth.xboxlive.com",
            "JWT"),
        MojangXboxJsonContext.Default.MojangXboxUserAuthRequest, xboxContract: true);

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangXboxToken> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        MojangRequests.EnsureSuccess(response, new Uri(Options.XboxUserAuth));
        return await MojangJson.LoadXboxTokenAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}

/// <summary>POST the XSTS authorization of a user token for one relying party; a 401 answer
/// fails as <see cref="MojangXstsDeniedException"/> carrying the refusal's shape.</summary>
public readonly record struct MojangXstsQuery(MojangClientOptions Options, string UserToken, string RelyingParty)
    : IApiOperation<MojangXboxToken>
{
    public HttpRequestMessage CreateRequest() => MojangRequests.Json(Options.XboxXstsAuthorize,
        new MojangXstsAuthRequest(
            new MojangXstsPropertiesRequest("RETAIL", [UserToken]),
            RelyingParty,
            "JWT"),
        MojangXboxJsonContext.Default.MojangXstsAuthRequest, xboxContract: true);

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangXboxToken> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new MojangXstsDeniedException(new Uri(Options.XboxXstsAuthorize),
                await MojangJson.LoadXstsDenialAsync(
                    await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false));
        }

        MojangRequests.EnsureSuccess(response, new Uri(Options.XboxXstsAuthorize));
        return await MojangJson.LoadXboxTokenAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}

// --------------------------------------------------------------------- minecraft services

/// <summary>POST launcher/login: the XSTS token (with its user hash) for the Minecraft access
/// token.</summary>
public readonly record struct MojangLauncherLoginQuery(MojangClientOptions Options, string UserHash, string XstsToken)
    : IApiOperation<MojangLauncherLogin>
{
    private string Url => MojangRequests.Services(Options, "/launcher/login");

    public HttpRequestMessage CreateRequest() => MojangRequests.Json(Url,
        new MojangLauncherLoginRequest
        {
            XToken = $"XBL3.0 x={UserHash};{XstsToken}",
            Platform = "PC_LAUNCHER",
        },
        MojangMicrosoftJsonContext.Default.MojangLauncherLoginRequest, xboxContract: false);

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangLauncherLogin> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        MojangRequests.EnsureSuccess(response, new Uri(Url));
        return await MojangJson.LoadLauncherLoginAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}

/// <summary>GET entitlements/license for one request id: the game ownership items. Any status
/// is read; a body that is not a JSON object reads as null.</summary>
public readonly record struct MojangEntitlementsQuery(MojangClientOptions Options, string AccessToken, string RequestId)
    : IApiOperation<MojangEntitlements?>
{
    private string Url => MojangRequests.Services(Options, "/entitlements/license?requestId=" + RequestId);

    public HttpRequestMessage CreateRequest() => MojangRequests.BearerGet(Url, AccessToken);

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangEntitlements?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        await MojangJson.LoadEntitlementsAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
}

/// <summary>GET minecraft/profile: the Java profile; null when the account has none
/// (HTTP 404).</summary>
public readonly record struct MojangProfileQuery(MojangClientOptions Options, string AccessToken)
    : IApiOperation<MojangProfile?>
{
    private string Url => MojangRequests.Services(Options, "/minecraft/profile");

    public HttpRequestMessage CreateRequest() => MojangRequests.BearerGet(Url, AccessToken);

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangProfile?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        MojangRequests.EnsureSuccess(response, new Uri(Url));
        return await MojangJson.LoadProfileAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}

/// <summary>POST minecraft/profile/skins: the skin PNG as multipart/form-data with its model
/// variant. Accepted statuses are 200/201/202/203/204/206; anything else fails as
/// InvalidOperationException("HTTP status &lt;code&gt; for &lt;url&gt;").</summary>
public readonly record struct MojangSkinUploadQuery(
    MojangClientOptions Options,
    string AccessToken,
    byte[] SkinData,
    string Variant) : IApiOperation<MojangProfileChanged>
{
    /// <summary>The skin upload endpoint URL.</summary>
    public string Url => MojangRequests.Services(Options, "/minecraft/profile/skins");

    public HttpRequestMessage CreateRequest()
    {
        var content = new MultipartFormDataContent("flint-" + Guid.NewGuid().ToString("N"));
        var skin = new ByteArrayContent(SkinData);
        skin.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(skin, "file", "skin.png");
        content.Add(new StringContent(Variant), "variant");

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Url)) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AccessToken}");
        return request;
    }

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public Task<MojangProfileChanged> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var statusCode = (int)response.StatusCode;
        if (statusCode is not 200 and not 201 and not 202 and not 203 and not 204 and not 206)
        {
            throw new InvalidOperationException($"HTTP status {statusCode} for {new Uri(Url)}");
        }

        return Task.FromResult(new MojangProfileChanged());
    }
}

/// <summary>PUT minecraft/profile/capes/active with {"capeId":"&lt;id&gt;"}; an empty cape id
/// is the bare DELETE that hides the active cape. A non-2xx status fails.</summary>
public readonly record struct MojangCapeChangeQuery(MojangClientOptions Options, string AccessToken, string CapeId)
    : IApiOperation<MojangProfileChanged>
{
    public HttpRequestMessage CreateRequest()
    {
        var url = MojangRequests.Services(Options, "/minecraft/profile/capes/active");
        var request = CapeId.Length == 0
            ? new HttpRequestMessage(HttpMethod.Delete, url)
            : new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new MojangCapeChangeRequest(CapeId),
                        MojangServicesJsonContext.Default.MojangCapeChangeRequest),
                    new MediaTypeHeaderValue("application/json")),
            };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AccessToken}");
        return request;
    }

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangProfileChanged> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return new MojangProfileChanged();
    }
}

/// <summary>DELETE minecraft/profile/skins/active: resets the profile to the default skin. A
/// non-2xx status fails.</summary>
public readonly record struct MojangSkinDeleteQuery(MojangClientOptions Options, string AccessToken)
    : IApiOperation<MojangProfileChanged>
{
    public HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            MojangRequests.Services(Options, "/minecraft/profile/skins/active"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AccessToken}");
        return request;
    }

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<MojangProfileChanged> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return new MojangProfileChanged();
    }
}

// ------------------------------------------------------------------------------ textures

/// <summary>GET the skin texture a profile points at: the image bytes, null on a non-2xx
/// status.</summary>
public readonly record struct MojangTextureQuery(Uri Url) : IApiOperation<byte[]?>
{
    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, Url);

    public Exception WrapFailure(Exception failure) =>
        failure is MojangApiException ? failure : new MojangApiException(failure.Message, failure);

    public async Task<byte[]?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        response.IsSuccessStatusCode
            ? await MojangRequests.ReadBodyAsync(response, ct).ConfigureAwait(false)
            : null;
}

// ------------------------------------------------------------------------------ groups

/// <summary>The Microsoft identity endpoints of <see cref="MojangClient"/>; every grant
/// takes the client id its flow captured.</summary>
public readonly struct MojangMicrosoftQueries(MojangClient root)
{
    /// <summary>The device authorization start.</summary>
    public MojangDeviceAuthorizationQuery DeviceAuthorization(string clientId) => new(root.Options, clientId);

    /// <summary>One device-code token poll.</summary>
    public MojangDeviceTokenQuery DeviceToken(string clientId, string deviceCode) =>
        new(root.Options, clientId, deviceCode);

    /// <summary>The refresh-token grant.</summary>
    public MojangRefreshTokenQuery RefreshToken(string clientId, string refreshToken) =>
        new(root.Options, clientId, refreshToken);

    /// <summary>The authorization-code grant of the interactive flow.</summary>
    public MojangAuthorizationCodeQuery AuthorizationCode(string clientId, string code, string redirectUri) =>
        new(root.Options, clientId, code, redirectUri);

    /// <summary>The interactive authorization URL the user's browser opens
    /// (QOAuth2AuthorizationCodeFlow::buildAuthenticateUrl with prompt=select_account).</summary>
    public string AuthorizeUrl(string clientId, string redirectUri) =>
        root.Options.MsaAuthorize +
        "?response_type=code" +
        "&client_id=" + Uri.EscapeDataString(clientId) +
        "&scope=" + Uri.EscapeDataString(MojangRequests.XboxLiveScopes) +
        "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
        "&prompt=select_account";
}

/// <summary>The Xbox Live endpoints of <see cref="MojangClient"/>.</summary>
public readonly struct MojangXboxQueries(MojangClient root)
{
    /// <summary>The user authentication with an MSA access token.</summary>
    public MojangXboxUserQuery UserAuthenticate(string msaAccessToken) => new(root.Options, msaAccessToken);

    /// <summary>The XSTS authorization of a user token for one relying party.</summary>
    public MojangXstsQuery XstsAuthorize(string userToken, string relyingParty) =>
        new(root.Options, userToken, relyingParty);
}

/// <summary>The Minecraft services endpoints of <see cref="MojangClient"/>.</summary>
public readonly struct MojangServicesQueries(MojangClient root)
{
    /// <summary>The launcher login with the XSTS token and its user hash.</summary>
    public MojangLauncherLoginQuery LauncherLogin(string userHash, string xstsToken) =>
        new(root.Options, userHash, xstsToken);

    /// <summary>The entitlements of the access token's account.</summary>
    public MojangEntitlementsQuery Entitlements(string accessToken, string requestId) =>
        new(root.Options, accessToken, requestId);

    /// <summary>The Java profile of the access token's account.</summary>
    public MojangProfileQuery Profile(string accessToken) => new(root.Options, accessToken);

    /// <summary>A skin upload with its model variant.</summary>
    public MojangSkinUploadQuery UploadSkin(string accessToken, byte[] skinData, string variant) =>
        new(root.Options, accessToken, skinData, variant);

    /// <summary>The active cape change; an empty id hides the cape.</summary>
    public MojangCapeChangeQuery ChangeCape(string accessToken, string capeId) =>
        new(root.Options, accessToken, capeId);

    /// <summary>The active skin reset.</summary>
    public MojangSkinDeleteQuery DeleteSkin(string accessToken) => new(root.Options, accessToken);
}

/// <summary>The skin texture download of <see cref="MojangClient"/>.</summary>
public readonly struct MojangTextureQueries
{
    /// <summary>The texture at a profile's skin URL.</summary>
    public MojangTextureQuery Get(Uri url) => new(url);
}
