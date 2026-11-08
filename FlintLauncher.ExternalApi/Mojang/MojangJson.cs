// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// The account services' answers with their binding — the response half of
// launcher/minecraft/auth/Parsers.{h,cpp}. Three wire dialects, one context each: the Xbox
// endpoints' exact PascalCase names, the Microsoft identity platform's snake_case and the
// Minecraft services' camelCase. The converters carry the upstream member tolerance
// (a missing or junk member reads as ""/0, a table entry with a junk member is skipped).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi.Mojang;

/// <summary>An Xbox token grant (user authentication and XSTS authorization): the instants,
/// the token and the display claims of the first xui entry carrying a user hash ("uhs"
/// included).</summary>
public sealed record MojangXboxToken
{
    [JsonConverter(typeof(MojangTimestampConverter))]
    public required DateTimeOffset IssueInstant { get; init; }

    [JsonConverter(typeof(MojangTimestampConverter))]
    public required DateTimeOffset NotAfter { get; init; }

    public required string Token { get; init; }

    [JsonPropertyName("DisplayClaims")]
    public required MojangDisplayClaims DisplayClaims { get; init; }

    /// <summary>The display claims of the first xui entry carrying a user hash ("uhs"
    /// included) — the flow's claims; <see cref="MojangJson.LoadXboxTokenAsync"/> rejects an
    /// answer without one.</summary>
    public IReadOnlyDictionary<string, string> Claims =>
        DisplayClaims.Xui.FirstOrDefault(entry => entry.ContainsKey("uhs"))
        ?? throw new JsonException("Missing uhs");
}

/// <summary>The DisplayClaims member: the xui claim entries as the answer lists them.</summary>
public sealed record MojangDisplayClaims
{
    [JsonPropertyName("xui")]
    public List<Dictionary<string, string>> Xui { get; init; } = [];
}

/// <summary>The shape of an XSTS 401 refusal: the code, or why the body could not name
/// one.</summary>
public abstract record MojangXstsAnswer;

/// <summary>A refusal carrying its Xbox Live error code.</summary>
public sealed record MojangXstsDenied(long XErr) : MojangXstsAnswer;

/// <summary>A refusal whose JSON object carries no numeric XErr.</summary>
public sealed record MojangXstsDenialWithoutCode : MojangXstsAnswer;

/// <summary>A refusal whose body is not a JSON object.</summary>
public sealed record MojangXstsDenialUnreadable : MojangXstsAnswer;

/// <summary>The answer of the Microsoft token endpoint: a grant, or an OAuth refusal
/// ("authorization_pending", "slow_down", ...).</summary>
public abstract record MojangTokenAnswer;

/// <summary>A token grant; <paramref name="RefreshToken"/> is null for grants that issue
/// none, <paramref name="Extra"/> holds every non-null member of the
/// document.</summary>
public sealed record MojangTokenGranted(
    string AccessToken,
    string? RefreshToken,
    int ExpiresIn,
    IReadOnlyDictionary<string, JsonElement> Extra) : MojangTokenAnswer;

/// <summary>An OAuth refusal: the error code and its description.</summary>
public sealed record MojangTokenRefused(string Error, string ErrorDescription) : MojangTokenAnswer;

/// <summary>The token endpoint's document before an endpoint applies its refusal rule: the
/// grant and the error shape share it — whichever side the answer is, the other side's
/// members read null. The extension bag keeps the members the typed ones do not
/// carry.</summary>
public sealed class MojangTokenDocument
{
    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public int? ExpiresIn { get; set; }

    public string? Error { get; set; }

    public string? ErrorDescription { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>The device authorization start of RFC 8628: the codes, the verification URI, the
/// lifetime in seconds and the polling interval (null keeps the client's default). The
/// error members ride the same shape — an OAuth refusal shares this endpoint's answer, so
/// every member is optional.</summary>
public sealed record MojangDeviceAuthorization(
    string? DeviceCode = null,
    string? UserCode = null,
    string? VerificationUri = null,
    int? ExpiresIn = null,
    int? Interval = null,
    string? Error = null,
    string? ErrorDescription = null);

/// <summary>A launcher login grant: the Minecraft access token and its lifetime in
/// seconds.</summary>
public sealed record MojangLauncherLogin
{
    public required string AccessToken { get; init; }

    public required double ExpiresIn { get; init; }

    /// <summary>Present on the wire and validated, though the flow only checks it.</summary>
    public required string Username { get; init; }
}

/// <summary>POST launcher/login — the XSTS token with its user hash.</summary>
public sealed record MojangLauncherLoginRequest
{
    [JsonPropertyName("xtoken")]
    public required string XToken { get; init; }

    public required string Platform { get; init; }
}

/// <summary>POST user/authenticate — the MSA access token as the RPS ticket, "d=" prefixed
/// (the Microsoft account token form the Xbox endpoint expects).</summary>
public sealed record MojangXboxUserAuthRequest(
    MojangRpsPropertiesRequest Properties,
    string RelyingParty,
    string TokenType);

public sealed record MojangRpsPropertiesRequest(
    string AuthMethod,
    string SiteName,
    string RpsTicket);

/// <summary>POST xsts/authorize — the user token for one relying party.</summary>
public sealed record MojangXstsAuthRequest(
    MojangXstsPropertiesRequest Properties,
    string RelyingParty,
    string TokenType);

public sealed record MojangXstsPropertiesRequest(
    string SandboxId,
    IReadOnlyList<string> UserTokens);

/// <summary>PUT capes/active — the cape to make active.</summary>
public sealed record MojangCapeChangeRequest(string CapeId);

/// <summary>The entitlements answer: the "items" entries as the answer lists them.</summary>
public sealed record MojangEntitlements
{
    public List<MojangEntitlementItem>? Items { get; init; }

    /// <summary>The names of the entitlement items.</summary>
    public IReadOnlyList<string> ItemNames =>
        Items?.Select(item => item.Name).ToList() ?? [];
}

/// <summary>One entitlements entry.</summary>
public sealed record MojangEntitlementItem
{
    public required string Name { get; init; }
}

/// <summary>A profile's active skin.</summary>
public sealed record MojangSkin(string Id, string Url, string Variant);

/// <summary>One of a profile's capes.</summary>
public sealed record MojangCape(string Id, string Url, string Alias);

/// <summary>The profile's cape table: the capes listed in full (id, url and alias all
/// readable) and the id of the active one (which counts even when its own url or alias is
/// unreadable; null when none is active).</summary>
public sealed record MojangCapeTable
{
    public IReadOnlyList<MojangCape> Entries { get; init; } = [];

    public string? ActiveCapeId { get; init; }
}

/// <summary>The Minecraft Java profile: the skins and capes tables as the answer lists
/// them, the active skin and the cape table selected from them.</summary>
public sealed record MojangProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public List<MojangSkinEntry>? Skins { get; init; }

    public List<MojangCapeEntry>? Capes { get; init; }

    /// <summary>The first fully present ACTIVE skin; null when the answer lists none.</summary>
    public MojangSkin? ActiveSkin
    {
        get
        {
            foreach (var skin in Skins ?? [])
            {
                if (skin is { Id: { } id, State: "ACTIVE", Url: { } url, Variant: { } variant })
                {
                    return new MojangSkin(id, url, variant);
                }
            }

            return null;
        }
    }

    /// <summary>The cape table: the fully present capes, and the id of the last ACTIVE one
    /// (which counts even when its own url or alias is absent).</summary>
    public MojangCapeTable CapeTable
    {
        get
        {
            string? activeCapeId = null;
            var entries = new List<MojangCape>();
            foreach (var cape in Capes ?? [])
            {
                if (cape is not { Id: not null, State: not null })
                {
                    continue;
                }

                if (cape.State == "ACTIVE")
                {
                    activeCapeId = cape.Id;
                }

                if (cape is { Url: not null, Alias: not null })
                {
                    entries.Add(new MojangCape(cape.Id, cape.Url, cape.Alias));
                }
            }

            return new MojangCapeTable { Entries = entries, ActiveCapeId = activeCapeId };
        }
    }
}

/// <summary>One skins entry of the profile answer; every member is optional (null when
/// absent).</summary>
public sealed record MojangSkinEntry
{
    public string? Id { get; init; }

    public string? State { get; init; }

    public string? Url { get; init; }

    public string? Variant { get; init; }
}

/// <summary>One capes entry of the profile answer, read like
/// <see cref="MojangSkinEntry"/>.</summary>
public sealed record MojangCapeEntry
{
    public string? Id { get; init; }

    public string? State { get; init; }

    public string? Url { get; init; }

    public string? Alias { get; init; }
}

/// <summary>A skin or cape change the service accepted; such an answer carries nothing to
/// read.</summary>
public readonly record struct MojangProfileChanged;

[JsonSourceGenerationOptions(RespectNullableAnnotations = true)]
[JsonSerializable(typeof(MojangXboxToken))]
[JsonSerializable(typeof(MojangXstsErrorBody))]
[JsonSerializable(typeof(MojangXboxUserAuthRequest))]
[JsonSerializable(typeof(MojangXstsAuthRequest))]
internal sealed partial class MojangXboxJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MojangTokenDocument))]
[JsonSerializable(typeof(MojangDeviceAuthorization))]
[JsonSerializable(typeof(MojangLauncherLogin))]
[JsonSerializable(typeof(MojangLauncherLoginRequest))]
internal sealed partial class MojangMicrosoftJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MojangEntitlements))]
[JsonSerializable(typeof(MojangProfile))]
[JsonSerializable(typeof(MojangCapeChangeRequest))]
public sealed partial class MojangServicesJsonContext : JsonSerializerContext;

/// <summary>The XSTS refusal body: every member optional, XErr bound raw so a non-numeric
/// member reads as absent (the "missing" refusal) rather than a failed read.</summary>
internal sealed class MojangXstsErrorBody
{
    public string? Identity { get; set; }

    public JsonElement? XErr { get; set; }

    public string? Message { get; set; }
}

/// <summary>Parsers::getDateTime (Qt::ISODate): a stated offset is kept as stated, a
/// timestamp that states none is read as UTC (AssumeUniversal).</summary>
internal sealed class MojangTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            throw new JsonException("the member is not a timestamp");
        }

        return timestamp;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
}

/// <summary>The decode entry points the operations read responses through: each binds its
/// record straight from the response stream and applies the endpoint's own validation.</summary>
public static class MojangJson
{
    /// <summary>An Xbox token grant; a member of the wrong kind — or an xui table without a
    /// user-hash entry — fails with <see cref="JsonException"/>.</summary>
    public static async Task<MojangXboxToken> LoadXboxTokenAsync(Stream body, CancellationToken ct)
    {
        var token = await JsonSerializer.DeserializeAsync(body, MojangXboxJsonContext.Default.MojangXboxToken, ct)
            .ConfigureAwait(false)
            ?? throw new JsonException("the response body is null");
        // force the uhs rule now, so a user-hash-less answer fails as a decode of this
        // answer instead of escaping the loader
        _ = token.Claims;
        return token;
    }

    /// <summary>The body of an XSTS 401 refusal.</summary>
    public static async Task<MojangXstsAnswer> LoadXstsDenialAsync(Stream body, CancellationToken ct)
    {
        try
        {
            var error = await JsonSerializer.DeserializeAsync(body,
                MojangXboxJsonContext.Default.MojangXstsErrorBody, ct).ConfigureAwait(false);
            return error?.XErr is { ValueKind: JsonValueKind.Number } xErr
                ? new MojangXstsDenied((long)xErr.GetDouble())
                : new MojangXstsDenialWithoutCode();
        }
        catch (JsonException)
        {
            return new MojangXstsDenialUnreadable();
        }
    }

    /// <summary>The device authorization start. An OAuth error document fails with its
    /// description (or code); a body without the required members fails with
    /// <see cref="JsonException"/>.</summary>
    public static async Task<MojangDeviceAuthorization> LoadDeviceAuthorizationAsync(Stream body,
        CancellationToken ct)
    {
        var authorization = await ReadTolerantAsync(body, MojangMicrosoftJsonContext.Default
            .MojangDeviceAuthorization, ct).ConfigureAwait(false) ?? new MojangDeviceAuthorization();
        if (authorization.Error is not null || authorization.ErrorDescription is not null)
        {
            throw new InvalidOperationException(authorization.ErrorDescription ??
                authorization.Error ?? "");
        }

        if (authorization.DeviceCode is null || authorization.UserCode is null ||
            authorization.VerificationUri is null || authorization.ExpiresIn is null)
        {
            throw new JsonException("required fields missing");
        }

        return authorization;
    }

    /// <summary>The token endpoint's document (the grant and the error shape share it, every
    /// member reading the tolerant fallback, the extension bag carrying the rest); a body
    /// that is not a JSON object reads as the empty document.</summary>
    public static async Task<MojangTokenDocument> LoadTokenDocumentAsync(Stream body, CancellationToken ct) =>
        await ReadTolerantAsync(body, MojangMicrosoftJsonContext.Default.MojangTokenDocument, ct)
            .ConfigureAwait(false)
        ?? new MojangTokenDocument();

    /// <summary>A launcher login grant (Parsers::parseMojangResponse); a missing or malformed
    /// member fails with <see cref="JsonException"/>.</summary>
    public static async Task<MojangLauncherLogin> LoadLauncherLoginAsync(Stream body, CancellationToken ct) =>
        await JsonSerializer.DeserializeAsync(body, MojangMicrosoftJsonContext.Default.MojangLauncherLogin, ct)
            .ConfigureAwait(false)
        ?? throw new JsonException("the response body is null");

    /// <summary>The entitlements answer; null when the body is not a JSON object.</summary>
    public static async Task<MojangEntitlements?> LoadEntitlementsAsync(Stream body, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(body,
                MojangServicesJsonContext.Default.MojangEntitlements, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The profile answer (Parsers::parseMinecraftProfile); a missing or malformed
    /// member fails with <see cref="JsonException"/>.</summary>
    public static async Task<MojangProfile> LoadProfileAsync(Stream body, CancellationToken ct) =>
        await JsonSerializer.DeserializeAsync(body, MojangServicesJsonContext.Default.MojangProfile, ct)
            .ConfigureAwait(false)
        ?? throw new JsonException("the response body is null");

    /// <summary>A body that is not a JSON object — garbage, an array, a bare token — reads as
    /// null (the upstream tolerance of the shared error/grant endpoints, whose every member
    /// reads its fallback).</summary>
    private static async Task<T?> ReadTolerantAsync<T>(Stream body, JsonTypeInfo<T> typeInfo,
        CancellationToken ct) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(body, typeInfo, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
