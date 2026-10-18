// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/AccountData.{h,cpp}.
//
// Serialization note (docs/JSON-MIGRATION.md, the accounts.json batch): the wire is the
// typed record set in AccountWire.cs. SaveState()/ResumeStateFromV3() map between the
// ledger (Token/Skin/Cape/MinecraftProfile, the data types below) and those records; the
// C++ per-field lenient reads that were probing QJsonValue tokens are now either record
// semantics (null member = field absent) or mapper validation (the mandatory profile
// attributes, the entitlement fallback). The write shape is ours — standard indented JSON
// via AuthJsonContext; the former Qt sorted-keys byte shape is retired with its byte gate.
//
// Deviations from the C++ original:
//  - Token::extra is a QVariantMap upstream; it stores the Xbox display claims (strings) and,
//    for the MSA token, the whole token-endpoint response flattened to a variant map (which
//    mixes strings and the numeric expires_in). The port stores <see cref="Token.Extra"/> as
//    Dictionary{string,JsonElement} — an owned value bag; the key order on disk is insertion
//    order (the write shape is ours, the QMap sorted-keys note is retired with the byte gate).
//  - QDateTime validity is expressed through nullable DateTimes (Qt uses an invalid QDateTime).
//  - QNetworkReply::NetworkError is ported as the int code of the mirrored QtNetworkError enum
//    (see AuthNetUtils); 0 == NoError.
//  - profileName()'s tr("No Minecraft profile") placeholder is kept as the English literal
//    (the launcher core is not localized).
//  - qWarning()/qDebug() diagnostics are dropped (comment markers where they carried meaning).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Auth;

public enum Validity
{
    None,
    Assumed,
    Certain,
}

/// <summary>Port of AccountData::Token.</summary>
public sealed class Token
{
    /// <summary>QDateTime issueInstant ("iat" when saved, epoch seconds).</summary>
    public DateTime? IssueInstant;

    /// <summary>QDateTime notAfter ("exp" when saved, epoch seconds).</summary>
    public DateTime? NotAfter;

    /// <summary>C++ member token (named Value: NO member — field or property — may carry
    /// the name of its enclosing type Token, CS0542).</summary>
    public string Value { get; set; } = "";

    /// <summary>C++ member refresh_token.</summary>
    public string RefreshToken = "";

    /// <summary>QVariantMap extra; values are owned immutable JSON values (strings/numbers/
    /// objects). Storing <see cref="JsonElement"/>s (not nodes) keeps the bag a value bag:
    /// no DeepClone on copy, no shared mutable trees between the auth steps and the ledger.</summary>
    public Dictionary<string, JsonElement> Extra = new();

    public Validity Validity = Validity.None;

    public bool Persistent = true;

    public Token Clone()
    {
        var copy = new Token
        {
            IssueInstant = IssueInstant,
            NotAfter = NotAfter,
            Value = Value,
            RefreshToken = RefreshToken,
            Validity = Validity,
            Persistent = Persistent,
            Extra = new Dictionary<string, JsonElement>(Extra),
        };
        return copy;
    }
}

/// <summary>Port of AccountData::Skin.</summary>
public sealed class Skin
{
    public string Id = "";
    public string Url = "";
    public string Variant = "";

    public byte[] Data = [];
}

/// <summary>Port of AccountData::Cape.</summary>
public sealed class Cape
{
    public string Id = "";
    public string Url = "";
    public string Alias = "";

    public byte[] Data = [];
}

/// <summary>Port of AccountData::MinecraftEntitlement.</summary>
public sealed class MinecraftEntitlement
{
    public bool OwnsMinecraft;

    public bool CanPlayMinecraft;

    public Validity Validity = Validity.None;
}

/// <summary>Port of AccountData::MinecraftProfile. Capes keep the QMap ordering (sorted by id).</summary>
public sealed class MinecraftProfile
{
    public string Id = "";
    public string Name = "";
    public Skin SkinField = new();
    public string CurrentCape = "";
    public SortedDictionary<string, Cape> Capes { get; } = new(StringComparer.Ordinal);
    public Validity Validity = Validity.None;
}

public enum AccountType
{
    Msa,
    Offline,
}

public enum AccountState
{
    Unchecked,
    Offline,
    Working,
    Online,
    Disabled,
    Errored,
    Expired,
    Gone,
}

/// <summary>Port of AccountData.</summary>
public sealed class AccountData
{
    /// <summary>Port of saveState(): the per-account wire document ("type", "msa-client-id",
    /// "msa", "utoken", "xrp-mc", "ygg", "profile", "entitlement" members).</summary>
    public AccountJson SaveState()
    {
        var wire = new AccountJson
        {
            Type = Type == AccountType.Msa ? "MSA" : "Offline",
            Ygg = TokenToWire(YggdrasilToken),
            Profile = ProfileToWire(MinecraftProfile),
            Entitlement = EntitlementToWire(MinecraftEntitlement),
        };
        if (Type == AccountType.Msa)
        {
            wire = wire with
            {
                MsaClientId = MsaClientId,
                Msa = TokenToWire(MsaToken),
                Utoken = TokenToWire(UserToken),
                XrpMc = TokenToWire(MojangServicesToken),
            };
        }

        return wire;
    }

    /// <summary>Port of resumeStateFromV3(): false when the account cannot be parsed (the
    /// type member is missing or unrecognized). Structural wrong-kind members never get
    /// here — they fail the wire deserialization, where AccountList skips the account.</summary>
    public bool ResumeStateFromV3(AccountJson wire)
    {
        switch (wire.Type)
        {
            case "MSA":
                Type = AccountType.Msa;
                break;
            case "Offline":
                Type = AccountType.Offline;
                break;
            default:
                // qWarning() << "Failed to parse account data: type is missing/not recognized.";
                return false;
        }

        if (Type == AccountType.Msa)
        {
            // an absent/empty client id stays empty (the C++ left msaClientID untouched)
            MsaClientId = wire.MsaClientId ?? "";
            MsaToken = TokenFromWire(wire.Msa);
            UserToken = TokenFromWire(wire.Utoken);
            MojangServicesToken = TokenFromWire(wire.XrpMc);
        }

        YggdrasilToken = TokenFromWire(wire.Ygg);
        // versions before 7.2 used "offline" as the offline token
        if (YggdrasilToken.Value == "offline")
        {
            YggdrasilToken.Value = "0";
        }

        MinecraftProfile = ProfileFromWire(wire.Profile);
        if (!EntitlementFromWire(wire.Entitlement, MinecraftEntitlement))
        {
            if (MinecraftProfile.Validity != Validity.None)
            {
                MinecraftEntitlement.CanPlayMinecraft = true;
                MinecraftEntitlement.OwnsMinecraft = true;
                MinecraftEntitlement.Validity = Validity.Assumed;
            }
        }

        Validity = MinecraftProfile.Validity;
        return true;
    }

    /// <summary>Yggdrasil access token, as passed to the game.</summary>
    public string AccessToken() => YggdrasilToken.Value;

    public string ProfileId() => MinecraftProfile.Id;

    public string ProfileName() => MinecraftProfile.Name.Length == 0 ? "No Minecraft profile" : MinecraftProfile.Name;

    public string LastError() => ErrorString;

    public AccountType Type = AccountType.Msa;

    public string MsaClientId = "";
    public Token MsaToken = new();
    public Token UserToken = new();
    public Token MojangServicesToken = new();

    public Token YggdrasilToken = new();
    public MinecraftProfile MinecraftProfile = new();
    public MinecraftEntitlement MinecraftEntitlement = new();
    public Validity Validity = Validity.None;

    // runtime only information (not saved with the account)
    public string InternalId = "";
    public string ErrorString = "";

    /// <summary>QNetworkReply::NetworkError storage; see AuthNetUtils for the code meanings.</summary>
    public int NetworkError;

    public AccountState AccountState = AccountState.Unchecked;

    // ------------------------------------------------------------------ helpers

    internal static JsonNode? Get(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) ? node : null;
    }

    // The QJsonValue probing helpers below serve the AUTH PROTOCOL responses (the MSA
    // device-code/token endpoints, the XSTS display claims — every caller is an auth step
    // or Parsers), not the accounts.json wire; that one is the typed AccountWire set.

    /// <summary>QJsonValue::isDouble() equivalent (missing key -> undefined -> false). Note:
    /// JsonValue.TryGetValue only matches the stored primitive type, so every numeric storage
    /// kind is probed (values created via JsonValue.Create(long) must read back as numbers).</summary>
    internal static bool IsNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return false;
        }

        return value.TryGetValue<double>(out _) || value.TryGetValue<long>(out _) ||
               value.TryGetValue<int>(out _) || value.TryGetValue<decimal>(out _) ||
               value.TryGetValue<float>(out _) || value.TryGetValue<short>(out _) ||
               value.TryGetValue<uint>(out _) || value.TryGetValue<ulong>(out _) ||
               value.TryGetValue<ushort>(out _) || value.TryGetValue<byte>(out _) ||
               value.TryGetValue<sbyte>(out _);
    }

    internal static bool IsString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out _);
    }

    internal static string GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
    }

    internal static double GetNumber(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (value.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }

            if (value.TryGetValue<decimal>(out var m))
            {
                return (double)m;
            }

            if (value.TryGetValue<float>(out var f))
            {
                return f;
            }
        }

        return 0;
    }

    /// <summary>QJsonValue::toInt() equivalent (0 for anything that is not an integral number).</summary>
    internal static int EnsureInteger(JsonObject obj, string key)
    {
        var node = Get(obj, key);
        if (IsNumber(node))
        {
            var d = GetNumber(node);
            return (int)d == d ? (int)d : 0;
        }

        return 0;
    }

    // ------------------------------------------------------------- (de)serialization

    /// <summary>The ledger token → wire (the C++ TokenToJsonv3). A non-persistent token
    /// saves nothing, and an empty token (nothing but iat/exp) saves nothing either.</summary>
    internal static TokenJson? TokenToWire(Token t)
    {
        if (!t.Persistent)
        {
            return null;
        }

        var wire = new TokenJson
        {
            Iat = t.IssueInstant is { } issueInstant
                ? new DateTimeOffset(issueInstant).ToUnixTimeSeconds()
                : null,
            Exp = t.NotAfter is { } notAfter
                ? new DateTimeOffset(notAfter).ToUnixTimeSeconds()
                : null,
            Token = t.Value.Length != 0 ? t.Value : null,
            RefreshToken = t.RefreshToken.Length != 0 ? t.RefreshToken : null,
            Extra = t.Extra.Count != 0 ? new Dictionary<string, JsonElement>(t.Extra) : null,
        };
        return wire.Token is not null || wire.RefreshToken is not null || wire.Extra is not null
            ? wire
            : null;
    }

    /// <summary>The wire token → ledger (the C++ TokenFromJsonv3): iat/exp are epoch
    /// seconds, a non-empty token grants Assumed validity, null extra entries drop.</summary>
    internal static Token TokenFromWire(TokenJson? wire)
    {
        var token = new Token();
        if (wire is null)
        {
            return token;
        }

        if (wire.Iat is { } iat)
        {
            token.IssueInstant = DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime;
        }

        if (wire.Exp is { } exp)
        {
            token.NotAfter = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        }

        if (wire.Token is { } value)
        {
            token.Value = value;
            token.Validity = Validity.Assumed;
        }

        if (wire.RefreshToken is { } refreshToken)
        {
            token.RefreshToken = refreshToken;
        }

        if (wire.Extra is { } extra)
        {
            foreach (var (key, element) in extra)
            {
                if (element.ValueKind != JsonValueKind.Null)
                {
                    token.Extra[key] = element;
                }
            }
        }

        return token;
    }

    /// <summary>The ledger profile → wire (the C++ ProfileToJsonv3): an id-less profile
    /// saves nothing; capes keep the QMap ordering (sorted by id, the SortedDictionary).</summary>
    internal static ProfileJson? ProfileToWire(MinecraftProfile p)
    {
        if (p.Id.Length == 0)
        {
            return null;
        }

        var capes = new List<CapeJson>();
        foreach (var cape in p.Capes.Values)
        {
            capes.Add(new CapeJson
            {
                Id = cape.Id,
                Url = cape.Url,
                Alias = cape.Alias,
                Data = cape.Data.Length != 0 ? Convert.ToBase64String(cape.Data) : null,
            });
        }

        return new ProfileJson
        {
            Id = p.Id,
            Name = p.Name,
            Cape = p.CurrentCape.Length != 0 ? p.CurrentCape : null,
            Skin = new SkinJson
            {
                Id = p.SkinField.Id,
                Url = p.SkinField.Url,
                Variant = p.SkinField.Variant,
                Data = p.SkinField.Data.Length != 0 ? Convert.ToBase64String(p.SkinField.Data) : null,
            },
            Capes = capes.ToArray(),
        };
    }

    /// <summary>The wire profile → ledger (the C++ ProfileFromJsonv3): the mandatory
    /// id/name/skin trio, optional textures data (lenient base64), the cape list and the
    /// current-cape reference check. A broken member yields the empty profile.</summary>
    internal static MinecraftProfile ProfileFromWire(ProfileJson? wire)
    {
        var profile = new MinecraftProfile();
        if (wire is null)
        {
            return profile;
        }

        if (string.IsNullOrEmpty(wire.Id) || string.IsNullOrEmpty(wire.Name))
        {
            // qWarning() << "mandatory profile attributes are missing or of unexpected type";
            return new MinecraftProfile();
        }

        if (wire.Skin is not { } skin || skin.Id is null || skin.Url is null || skin.Variant is null)
        {
            // qWarning() << "skin is missing / mandatory skin attributes are missing";
            return new MinecraftProfile();
        }

        profile.Name = wire.Name;
        profile.Id = wire.Id;
        profile.SkinField.Id = skin.Id;
        profile.SkinField.Url = RewriteTextureUrl(skin.Url);
        profile.SkinField.Variant = skin.Variant;
        if (skin.Data is { } skinData)
        {
            profile.SkinField.Data = FromBase64(skinData);
        }

        foreach (var capeWire in wire.Capes ?? [])
        {
            if (capeWire.Id is null || capeWire.Url is null || capeWire.Alias is null)
            {
                // qWarning() << "cape is not an object / mandatory attributes are missing";
                return new MinecraftProfile();
            }

            var cape = new Cape
            {
                Id = capeWire.Id,
                Url = RewriteTextureUrl(capeWire.Url),
                Alias = capeWire.Alias,
            };
            if (capeWire.Data is { } capeData)
            {
                cape.Data = FromBase64(capeData);
            }

            profile.Capes[cape.Id] = cape;
        }

        // current cape only sticks when it names a cape of the list
        if (wire.Cape is { } currentCape && profile.Capes.ContainsKey(currentCape))
        {
            profile.CurrentCape = currentCape;
        }

        profile.Validity = Validity.Assumed;
        return profile;
    }

    /// <summary>The wire entitlement → ledger (the C++ EntitlementFromJsonv3): false when
    /// the object is absent or either boolean is missing.</summary>
    internal static bool EntitlementFromWire(EntitlementJson? wire, MinecraftEntitlement outEntitlement)
    {
        if (wire is null || wire.OwnsMinecraft is not { } owns || wire.CanPlayMinecraft is not { } canPlay)
        {
            return false;
        }

        outEntitlement.CanPlayMinecraft = canPlay;
        outEntitlement.OwnsMinecraft = owns;
        outEntitlement.Validity = Validity.Assumed;
        return true;
    }

    /// <summary>The ledger entitlement → wire (the C++ EntitlementToJsonv3): a None-validity
    /// entitlement saves nothing.</summary>
    internal static EntitlementJson? EntitlementToWire(MinecraftEntitlement p)
    {
        return p.Validity == Validity.None
            ? null
            : new EntitlementJson { OwnsMinecraft = p.OwnsMinecraft, CanPlayMinecraft = p.CanPlayMinecraft };
    }

    /// <summary>The http->https textures rewrite, shared by profile parsing and resume.</summary>
    internal static string RewriteTextureUrl(string url)
    {
        return url.Replace("http://textures.minecraft.net", "https://textures.minecraft.net");
    }

    /// <summary>QByteArray::fromBase64 (default lenient mode: whitespace/invalid chars skipped).</summary>
    internal static byte[] FromBase64(string str)
    {
        var sb = new StringBuilder(str.Length);
        foreach (var c in str)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')
            {
                sb.Append(c);
            }
        }

        return Convert.FromBase64String(sb.ToString());
    }

    // -------------------------------------------------------- element-bag helpers

    /// <summary>Reads a string out of the extra bag (QJsonValue::toString("") semantics;
    /// a default-initialized element reads as "").</summary>
    internal static string GetString(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
    }

    /// <summary>An owned immutable copy of a node as an extra-bag value (the former
    /// DeepClone; the JSON text round-trip keeps the exact value).</summary>
    internal static JsonElement ToElement(JsonNode? node)
    {
        return JsonDocument.Parse(node?.ToJsonString() ?? "null").RootElement.Clone();
    }

    /// <summary>An extra-bag string value built without touching the reflection serializer
    /// (written through the raw UTF-8 writer, parsed back through the source-gen context).</summary>
    internal static JsonElement StringElement(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(Encoding.UTF8.GetMaxByteCount(value.Length) + 16);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        return JsonSerializer.Deserialize(buffer.WrittenSpan, AuthJsonContext.Default.JsonElement);
    }
}
