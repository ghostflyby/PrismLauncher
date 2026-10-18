// SPDX-License-Identifier: GPL-3.0-only
// The accounts.json wire domain (docs/JSON-MIGRATION.md batch "accounts.json 读写类型化"):
// the typed probe records over the format AccountData::saveState/resumeStateFromV3 speak.
//
// Read compatibility (files written by upstream Prism land here verbatim through the
// migration): every member is nullable and the semantic rules of the C++ read (the
// mandatory profile attributes, the entitlement fallback, the legacy "offline" ygg token,
// the lenient base64) live in the AccountData mappers. Only formatVersion needs a custom
// converter — the C++ read it as QVariant::toInt, which also accepts digit strings.
// A structurally wrong-kind member (e.g. a profile that is not an object) fails the
// DESERIALIZATION of that account; AccountList skips the account and keeps loading the
// rest — the more reasonable failure behavior (the C++ half-loaded such accounts with
// silently empty members).
//
// Write shape is free: the context writes standard indented JSON with null members
// omitted (the former Qt sorted-keys + 4-space-indent tail has no external consumer;
// the byte gate against it is retired). The record layout below IS the on-disk contract
// of our own file now.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlintLauncher.Core.Auth;

/// <summary>The accounts.json root: formatVersion 3 (MojangMsa) + the account array.</summary>
public sealed record AccountFileJson
{
    [JsonConverter(typeof(LenientFormatVersionConverter))]
    public int FormatVersion { get; init; }

    /// <summary>The raw per-account documents: they deserialize one by one so a
    /// structurally broken account is skipped instead of failing the whole file.</summary>
    public JsonElement[]? Accounts { get; init; }
}

/// <summary>The per-account document (AccountData::saveState key set + the list's
/// "active" flag).</summary>
public sealed record AccountJson
{
    /// <summary>"MSA" or "Offline"; anything else rejects the account (resumeStateFromV3).</summary>
    public string? Type { get; init; }

    [JsonPropertyName("msa-client-id")]
    public string? MsaClientId { get; init; }

    public TokenJson? Msa { get; init; }
    public TokenJson? Utoken { get; init; }

    [JsonPropertyName("xrp-mc")]
    public TokenJson? XrpMc { get; init; }

    public TokenJson? Ygg { get; init; }
    public ProfileJson? Profile { get; init; }
    public EntitlementJson? Entitlement { get; init; }

    /// <summary>The list layer's default-account flag (read leniently: a non-bool is no flag).</summary>
    public bool? Active { get; init; }
}

/// <summary>The token object ("msa"/"utoken"/"xrp-mc"/"ygg"). iat/exp are epoch seconds;
/// extra is the mixed-type claim bag (QVariantMap upstream) — read as raw elements, the
/// mapper skips null entries.</summary>
public sealed record TokenJson
{
    public long? Iat { get; init; }
    public long? Exp { get; init; }
    public string? Token { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The Minecraft profile. The mapper rejects (empty profile) documents missing
/// the mandatory id/name/skin trio, like resumeStateFromV3 did.</summary>
public sealed record ProfileJson
{
    public string? Id { get; init; }
    public string? Name { get; init; }

    /// <summary>The current cape id; only sticks when it names a cape of the cape list.</summary>
    public string? Cape { get; init; }

    public SkinJson? Skin { get; init; }
    public CapeJson[]? Capes { get; init; }
}

public sealed record SkinJson
{
    public string? Id { get; init; }
    public string? Url { get; init; }
    public string? Variant { get; init; }

    /// <summary>Optional; base64 with Qt's lenient decode (invalid chars skipped).</summary>
    public string? Data { get; init; }
}

public sealed record CapeJson
{
    public string? Id { get; init; }
    public string? Url { get; init; }
    public string? Alias { get; init; }

    public string? Data { get; init; }
}

/// <summary>The entitlement probe; both booleans must be present for the entitlement to
/// count (the C++ read), otherwise the profile-validity fallback applies.</summary>
public sealed record EntitlementJson
{
    public bool? OwnsMinecraft { get; init; }

    public bool? CanPlayMinecraft { get; init; }
}

/// <summary>QVariant::toInt over the formatVersion value (numbers and digit strings);
/// anything else reads 0 — the caller's version check sends that file to the
/// accounts-old.json rename branch.</summary>
public sealed class LenientFormatVersionConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var i) => i,
            JsonTokenType.String when int.TryParse(reader.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>The auth wire context: the accounts.json document types + the JsonElement
/// bag support (AOT red line: a missing root registration is a runtime failure). The
/// naming policy is the wire's camelCase; the kebab-case members carry explicit names.</summary>
[JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(AccountFileJson))]
[JsonSerializable(typeof(AccountJson))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
