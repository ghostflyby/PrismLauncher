// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/AccountData.{h,cpp}.
//
// Deviations from the C++ original:
//  - Token::extra is a QVariantMap upstream; it stores the Xbox display claims (strings) and,
//    for the MSA token, the whole token-endpoint response flattened to a variant map (which
//    mixes strings and the numeric expires_in). To keep the JSON byte shape on saveState()
//    the port stores <see cref="Token.Extra"/> as Dictionary{string,JsonNode} and re-sorts the
//    keys on save, reproducing the QVariantMap (QMap = sorted) -> QJsonObject ordering.
//  - QDateTime validity is expressed through nullable DateTimes (Qt uses an invalid QDateTime).
//  - QNetworkReply::NetworkError is ported as the int code of the mirrored QtNetworkError enum
//    (see AuthNetUtils); 0 == NoError.
//  - profileName()'s tr("No Minecraft profile") placeholder is kept as the English literal
//    (the launcher core is not localized).
//  - qWarning()/qDebug() diagnostics are dropped (comment markers where they carried meaning).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FlintLauncher.Core.Auth;

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;

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
    /// <summary>Port of saveState(): the per-account JSON object ("type", "msa-client-id",
    /// "msa", "utoken", "xrp-mc", "ygg", "profile", "entitlement" keys).</summary>
    public JsonObject SaveState()
    {
        var output = new JsonObject();
        if (Type == AccountType.Msa)
        {
            output["type"] = "MSA";
            output["msa-client-id"] = JsonValue.Create(MsaClientId);
            TokenToJsonv3(output, MsaToken, "msa");
            TokenToJsonv3(output, UserToken, "utoken");
            TokenToJsonv3(output, MojangServicesToken, "xrp-mc");
        }
        else if (Type == AccountType.Offline)
        {
            output["type"] = "Offline";
        }

        TokenToJsonv3(output, YggdrasilToken, "ygg");
        ProfileToJsonv3(output, MinecraftProfile, "profile");
        EntitlementToJsonv3(output, MinecraftEntitlement);
        return output;
    }

    /// <summary>Port of resumeStateFromV3(QJsonObject): false when the account cannot be parsed.</summary>
    public bool ResumeStateFromV3(JsonObject data)
    {
        var typeV = Get(data, "type");
        if (typeV is not JsonValue typeVal || !typeVal.TryGetValue(out string? typeS))
        {
            // qWarning() << "Failed to parse account data: type is missing.";
            return false;
        }

        if (typeS == "MSA")
        {
            Type = AccountType.Msa;
        }
        else if (typeS == "Offline")
        {
            Type = AccountType.Offline;
        }
        else
        {
            // qWarning() << "Failed to parse account data: type is not recognized.";
            return false;
        }

        if (Type == AccountType.Msa)
        {
            var clientIdv = Get(data, "msa-client-id");
            if (clientIdv is JsonValue clientIdVal && clientIdVal.TryGetValue(out string? clientId))
            {
                MsaClientId = clientId;
            } // leave msaClientID empty if it doesn't exist or isn't a string

            MsaToken = TokenFromJsonv3(data, "msa");
            UserToken = TokenFromJsonv3(data, "utoken");
            MojangServicesToken = TokenFromJsonv3(data, "xrp-mc");
        }

        YggdrasilToken = TokenFromJsonv3(data, "ygg");
        // versions before 7.2 used "offline" as the offline token
        if (YggdrasilToken.Value == "offline")
        {
            YggdrasilToken.Value = "0";
        }

        MinecraftProfile = ProfileFromJsonv3(data, "profile");
        if (!EntitlementFromJsonv3(data, MinecraftEntitlement))
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

    internal static void TokenToJsonv3(JsonObject parent, Token t, string tokenName)
    {
        if (!t.Persistent)
        {
            return;
        }

        var outObj = new JsonObject();
        if (t.IssueInstant is { } issueInstant)
        {
            outObj["iat"] = ((DateTimeOffset)issueInstant).ToUnixTimeSeconds();
        }

        if (t.NotAfter is { } notAfter)
        {
            outObj["exp"] = ((DateTimeOffset)notAfter).ToUnixTimeSeconds();
        }

        var save = false;
        if (t.Value.Length != 0)
        {
            outObj["token"] = t.Value;
            save = true;
        }

        if (t.RefreshToken.Length != 0)
        {
            outObj["refresh_token"] = t.RefreshToken;
            save = true;
        }

        if (t.Extra.Count != 0)
        {
            // QJsonObject::fromVariantMap(QVariantMap): keys sorted like QMap
            var extra = new JsonObject();
            foreach (var (key, value) in t.Extra.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                extra[key] = JsonNode.Parse(value.GetRawText());
            }

            outObj["extra"] = extra;
            save = true;
        }

        if (save)
        {
            parent[tokenName] = outObj;
        }
    }

    internal static Token TokenFromJsonv3(JsonObject parent, string tokenName)
    {
        var outToken = new Token();
        var tokenObject = Get(parent, tokenName) as JsonObject;
        if (tokenObject is null || tokenObject.Count == 0)
        {
            return outToken;
        }

        var issueInstant = Get(tokenObject, "iat");
        if (IsNumber(issueInstant))
        {
            outToken.IssueInstant = DateTimeOffset.FromUnixTimeMilliseconds((long)GetNumber(issueInstant) * 1000).UtcDateTime;
        }

        var notAfter = Get(tokenObject, "exp");
        if (IsNumber(notAfter))
        {
            outToken.NotAfter = DateTimeOffset.FromUnixTimeMilliseconds((long)GetNumber(notAfter) * 1000).UtcDateTime;
        }

        var token = Get(tokenObject, "token");
        if (IsString(token))
        {
            outToken.Value = GetString(token);
            outToken.Validity = Validity.Assumed;
        }

        var refreshToken = Get(tokenObject, "refresh_token");
        if (IsString(refreshToken))
        {
            outToken.RefreshToken = GetString(refreshToken);
        }

        var extra = Get(tokenObject, "extra");
        if (extra is JsonObject extraObj)
        {
            foreach (var (key, value) in extraObj)
            {
                if (value is not null)
                {
                    outToken.Extra[key] = ToElement(value);
                }
            }
        }

        return outToken;
    }

    internal static void ProfileToJsonv3(JsonObject parent, MinecraftProfile p, string tokenName)
    {
        if (p.Id.Length == 0)
        {
            return;
        }

        var outObj = new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
        };
        if (p.CurrentCape.Length != 0)
        {
            outObj["cape"] = p.CurrentCape;
        }

        {
            var skinObj = new JsonObject
            {
                ["id"] = p.SkinField.Id,
                ["url"] = p.SkinField.Url,
                ["variant"] = p.SkinField.Variant,
            };
            if (p.SkinField.Data.Length != 0)
            {
                skinObj["data"] = Convert.ToBase64String(p.SkinField.Data);
            }

            outObj["skin"] = skinObj;
        }

        var capesArray = new JsonArray();
        foreach (var cape in p.Capes.Values)
        {
            var capeObj = new JsonObject
            {
                ["id"] = cape.Id,
                ["url"] = cape.Url,
                ["alias"] = cape.Alias,
            };
            if (cape.Data.Length != 0)
            {
                capeObj["data"] = Convert.ToBase64String(cape.Data);
            }

            capesArray.Add((JsonNode?)capeObj);
        }

        outObj["capes"] = capesArray;
        parent[tokenName] = outObj;
    }

    /// <summary>The http->https textures rewrite, shared by profile parsing and resume.</summary>
    internal static string RewriteTextureUrl(string url)
    {
        return url.Replace("http://textures.minecraft.net", "https://textures.minecraft.net");
    }

    internal static MinecraftProfile ProfileFromJsonv3(JsonObject parent, string tokenName)
    {
        var outProfile = new MinecraftProfile();
        var tokenObject = Get(parent, tokenName) as JsonObject;
        if (tokenObject is null || tokenObject.Count == 0)
        {
            return outProfile;
        }

        {
            var idV = Get(tokenObject, "id");
            var nameV = Get(tokenObject, "name");
            if (!IsString(idV) || !IsString(nameV))
            {
                // qWarning() << "mandatory profile attributes are missing or of unexpected type";
                return new MinecraftProfile();
            }

            outProfile.Name = GetString(nameV);
            outProfile.Id = GetString(idV);
        }

        {
            var skinV = Get(tokenObject, "skin");
            if (skinV is not JsonObject skinObj)
            {
                // qWarning() << "skin is missing";
                return new MinecraftProfile();
            }

            var idV = Get(skinObj, "id");
            var urlV = Get(skinObj, "url");
            var variantV = Get(skinObj, "variant");
            if (!IsString(idV) || !IsString(urlV) || !IsString(variantV))
            {
                // qWarning() << "mandatory skin attributes are missing or of unexpected type";
                return new MinecraftProfile();
            }

            outProfile.SkinField.Id = GetString(idV);
            outProfile.SkinField.Url = RewriteTextureUrl(GetString(urlV));
            outProfile.SkinField.Variant = GetString(variantV);

            // data for skin is optional
            var dataV = Get(skinObj, "data");
            if (IsString(dataV))
            {
                // TODO: validate base64
                outProfile.SkinField.Data = FromBase64(GetString(dataV));
            }
            else if (dataV is not null)
            {
                // qWarning() << "skin data is something unexpected";
                return new MinecraftProfile();
            }
        }

        {
            var capesV = Get(tokenObject, "capes");
            if (capesV is not JsonArray capesArray)
            {
                // qWarning() << "capes is not an array!";
                return new MinecraftProfile();
            }

            foreach (var capeV in capesArray)
            {
                if (capeV is not JsonObject capeObj)
                {
                    // qWarning() << "cape is not an object!";
                    return new MinecraftProfile();
                }

                var idV = Get(capeObj, "id");
                var urlV = Get(capeObj, "url");
                var aliasV = Get(capeObj, "alias");
                if (!IsString(idV) || !IsString(urlV) || !IsString(aliasV))
                {
                    // qWarning() << "mandatory skin attributes are missing or of unexpected type";
                    return new MinecraftProfile();
                }

                var cape = new Cape
                {
                    Id = GetString(idV),
                    Url = RewriteTextureUrl(GetString(urlV)),
                    Alias = GetString(aliasV),
                };

                // data for cape is optional.
                var dataV = Get(capeObj, "data");
                if (IsString(dataV))
                {
                    // TODO: validate base64
                    cape.Data = FromBase64(GetString(dataV));
                }
                else if (dataV is not null)
                {
                    // qWarning() << "cape data is something unexpected";
                    return new MinecraftProfile();
                }

                outProfile.Capes[cape.Id] = cape;
            }
        }
        // current cape
        {
            var capeV = Get(tokenObject, "cape");
            if (IsString(capeV))
            {
                var currentCape = GetString(capeV);
                if (outProfile.Capes.ContainsKey(currentCape))
                {
                    outProfile.CurrentCape = currentCape;
                }
            }
        }
        outProfile.Validity = Validity.Assumed;
        return outProfile;
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

    internal static void EntitlementToJsonv3(JsonObject parent, MinecraftEntitlement p)
    {
        if (p.Validity == Validity.None)
        {
            return;
        }

        var outObj = new JsonObject
        {
            ["ownsMinecraft"] = p.OwnsMinecraft,
            ["canPlayMinecraft"] = p.CanPlayMinecraft,
        };
        parent["entitlement"] = outObj;
    }

    internal static bool EntitlementFromJsonv3(JsonObject parent, MinecraftEntitlement outEntitlement)
    {
        if (Get(parent, "entitlement") is not JsonObject entitlementObject || entitlementObject.Count == 0)
        {
            return false;
        }

        {
            var ownsMinecraftV = Get(entitlementObject, "ownsMinecraft");
            var canPlayMinecraftV = Get(entitlementObject, "canPlayMinecraft");
            if (!IsBool(ownsMinecraftV) || !IsBool(canPlayMinecraftV))
            {
                // qWarning() << "mandatory attributes are missing or of unexpected type";
                return false;
            }

            outEntitlement.CanPlayMinecraft = EnsureBool(canPlayMinecraftV);
            outEntitlement.OwnsMinecraft = EnsureBool(ownsMinecraftV);
            outEntitlement.Validity = Validity.Assumed;
        }
        return true;
    }

    internal static bool IsBool(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out _);
    }

    internal static bool EnsureBool(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : false;
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
