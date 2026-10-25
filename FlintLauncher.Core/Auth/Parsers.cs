// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/Parsers.{h,cpp}: the response parsers that drive the
// user-visible error strings of the auth flow.
//
// Deviations: input is the raw response bytes (like upstream) parsed with System.Text.Json;
// the "Failed to parse response from <service> as JSON" qWarnings are dropped (the bool return
// carries the outcome, as upstream). QDateTime::fromString(Qt::ISODate) maps to an invariant
// round-trip DateTime.Parse (the Xbox timestamps carry 7-digit fractions).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Auth;

public static class Parsers
{
    /// <summary>Port of Parsers::getDateTime (Qt::ISODate).</summary>
    public static bool GetDateTime(JsonNode? value, out DateTime outDateTime)
    {
        outDateTime = default;
        if (value is not JsonValue v || !v.TryGetValue(out string? s))
        {
            return false;
        }

        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        outDateTime = parsed;
        return true;
    }

    public static bool GetString(JsonNode? value, out string outString)
    {
        outString = "";
        if (value is not JsonValue v || !v.TryGetValue(out string? s))
        {
            return false;
        }

        outString = s;
        return true;
    }

    public static bool GetNumber(JsonNode? value, out double outNumber)
    {
        outNumber = 0;
        if (value is not JsonValue v || !v.TryGetValue(out double d))
        {
            return false;
        }

        outNumber = d;
        return true;
    }

    public static bool GetNumber(JsonNode? value, out long outNumber)
    {
        outNumber = 0;
        if (value is not JsonValue v || !v.TryGetValue(out double d))
        {
            return false;
        }

        outNumber = (long)d;
        return true;
    }

    public static bool GetBool(JsonNode? value, out bool outBool)
    {
        outBool = false;
        if (value is not JsonValue v || !v.TryGetValue(out bool b))
        {
            return false;
        }

        outBool = b;
        return true;
    }

    /*
    {
       "IssueInstant":"2020-12-07T19:52:08.4463796Z",
       "NotAfter":"2020-12-21T19:52:08.4463796Z",
       "Token":"token",
       "DisplayClaims":{
          "xui":[
             {
                "uhs":"userhash"
             }
          ]
       }
     }
    */
    // TODO: handle error responses ... (see XboxAuthorizationStep::processSTSError)
    /*
    {
        "Identity":"0",
        "XErr":2148916238,
        "Message":"",
        "Redirect":"https://start.ui.xboxlive.com/AddChildToFamily"
    }
    // 2148916233 = missing Xbox account
    // 2148916238 = child account not linked to a family
    */

    public static bool ParseXTokenResponse(byte[] data, Token output, string name)
    {
        var obj = TryParseObject(data);
        if (obj is null)
        {
            return false;
        }

        if (!GetDateTime(AccountData.Get(obj, "IssueInstant"), out var issueInstant))
        {
            // qWarning() << "User IssueInstant is not a timestamp";
            return false;
        }

        output.IssueInstant = issueInstant.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(issueInstant, DateTimeKind.Utc)
            : issueInstant.ToUniversalTime();
        if (!GetDateTime(AccountData.Get(obj, "NotAfter"), out var notAfter))
        {
            // qWarning() << "User NotAfter is not a timestamp";
            return false;
        }

        output.NotAfter = notAfter.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(notAfter, DateTimeKind.Utc)
            : notAfter.ToUniversalTime();
        if (!GetString(AccountData.Get(obj, "Token"), out var token))
        {
            // qWarning() << "User Token is not a string";
            return false;
        }

        output.Value = token;
        var arrayVal = (AccountData.Get(obj, "DisplayClaims") as JsonObject) is { } displayClaims
            ? AccountData.Get(displayClaims, "xui")
            : null;
        if (arrayVal is not JsonArray array)
        {
            // qWarning() << "Missing xui claims array";
            return false;
        }

        var foundUhs = false;
        foreach (var item in array)
        {
            if (item is not JsonObject claimObj)
            {
                continue;
            }

            if (claimObj.ContainsKey("uhs"))
            {
                foundUhs = true;
            }
            else
            {
                continue;
            }

            // consume all 'display claims' ... whatever that means
            foreach (var (key, claimValue) in claimObj)
            {
                if (!GetString(claimValue, out var claim))
                {
                    // qWarning() << "display claim" << key << "is not a string...";
                    return false;
                }

                output.Extra[key] = AccountData.StringElement(claim);
            }

            break;
        }

        if (!foundUhs)
        {
            // qWarning() << "Missing uhs";
            return false;
        }

        output.Validity = Validity.Certain;
        return true;
    }

    public static bool ParseMinecraftProfile(byte[] data, MinecraftProfile output)
    {
        var obj = TryParseObject(data);
        if (obj is null)
        {
            return false;
        }

        if (!GetString(AccountData.Get(obj, "id"), out var id))
        {
            // qWarning() << "Minecraft profile id is not a string";
            return false;
        }

        output.Id = id;

        if (!GetString(AccountData.Get(obj, "name"), out var name))
        {
            // qWarning() << "Minecraft profile name is not a string";
            return false;
        }

        output.Name = name;

        var skinsArray = AccountData.Get(obj, "skins") as JsonArray ?? new JsonArray();
        foreach (var skin in skinsArray)
        {
            if (skin is not JsonObject skinObj)
            {
                continue;
            }

            var skinOut = new Skin();
            if (!GetString(AccountData.Get(skinObj, "id"), out var skinId))
            {
                continue;
            }

            skinOut.Id = skinId;
            if (!GetString(AccountData.Get(skinObj, "state"), out var state))
            {
                continue;
            }

            if (state != "ACTIVE")
            {
                continue;
            }

            if (!GetString(AccountData.Get(skinObj, "url"), out var skinUrl))
            {
                continue;
            }

            skinOut.Url = AccountData.RewriteTextureUrl(skinUrl);
            if (!GetString(AccountData.Get(skinObj, "variant"), out var variant))
            {
                continue;
            }

            skinOut.Variant = variant;
            // we deal with only the active skin
            output.SkinField = skinOut;
            break;
        }

        var capesArray = AccountData.Get(obj, "capes") as JsonArray ?? new JsonArray();

        var currentCape = "";
        foreach (var cape in capesArray)
        {
            if (cape is not JsonObject capeObj)
            {
                continue;
            }

            var capeOut = new Cape();
            if (!GetString(AccountData.Get(capeObj, "id"), out var capeId))
            {
                continue;
            }

            capeOut.Id = capeId;
            if (!GetString(AccountData.Get(capeObj, "state"), out var capeState))
            {
                continue;
            }

            if (capeState == "ACTIVE")
            {
                currentCape = capeOut.Id;
            }

            if (!GetString(AccountData.Get(capeObj, "url"), out var capeUrl))
            {
                continue;
            }

            capeOut.Url = AccountData.RewriteTextureUrl(capeUrl);
            if (!GetString(AccountData.Get(capeObj, "alias"), out var alias))
            {
                continue;
            }

            capeOut.Alias = alias;

            output.Capes[capeOut.Id] = capeOut;
        }

        output.CurrentCape = currentCape;
        output.Validity = Validity.Certain;
        return true;
    }

    // these skin URLs are for the MHF_Steve and MHF_Alex accounts (made by a Mojang employee)
    // they are needed because the session server doesn't return skin urls for default skins
    private const string SkinUrlSteve =
        "https://textures.minecraft.net/texture/1a4af718455d4aab528e7a61f86fa25e6a369d1768dcb13f7df319a713eb810b";

    private const string SkinUrlAlex =
        "https://textures.minecraft.net/texture/83cee5ca6afcdb171285aa00e8049c297b2dbeba0efb8ff970a5677a1b644032";

    private static bool IsDefaultModelSteve(string uuid)
    {
        // need to calculate *Java* hashCode of UUID
        // if number is even, skin/model is steve, otherwise it is alex

        // just in case dashes are in the id
        uuid = uuid.Replace("-", "");

        if (uuid.Length != 32)
        {
            return true;
        }

        // we need to use unsigned numbers to guarantee truncation below
        if (!ulong.TryParse(uuid[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var most) ||
            !ulong.TryParse(uuid[^16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var least))
        {
            return true;
        }

        var xored = most ^ least;
        return ((uint)(xored >> 32) ^ (uint)xored) % 2 == 0;
    }

    /**
    Uses session server for skin/cape lookup instead of profile,
    because locked Mojang accounts cannot access profile endpoint
    (https://api.minecraftservices.com/minecraft/profile/)

    ref: https://wiki.vg/Mojang_API#UUID_to_Profile_and_Skin.2FCape

    {
        "id": "{profile identifier}",
        "name": "{player name}",
        "properties": [
            {
                "name": "textures",
                "value": "{base64 string}"
            }
        ]
    }

    decoded base64 "value":
    {
        "timestamp": {java time in ms},
        "profileId": "{profile uuid}",
        "profileName": "{player name}",
        "textures": {
            "SKIN": {
                "url": "{player skin URL}"
            },
            "CAPE": {
                "url": "{player cape URL}"
            }
        }
    }
    */
    public static bool ParseMinecraftProfileMojang(byte[] data, MinecraftProfile output)
    {
        var obj = TryParseObject(data);
        if (obj is null)
        {
            return false;
        }

        if (!GetString(AccountData.Get(obj, "id"), out var id))
        {
            return false;
        }

        output.Id = id;

        if (!GetString(AccountData.Get(obj, "name"), out var name))
        {
            return false;
        }

        output.Name = name;

        var propsArray = AccountData.Get(obj, "properties") as JsonArray ?? new JsonArray();
        byte[]? texturePayload = null;
        foreach (var p in propsArray)
        {
            if (p is not JsonObject pObj)
            {
                continue;
            }

            var nameV = AccountData.Get(pObj, "name");
            if (!AccountData.IsString(nameV) || AccountData.GetString(nameV) != "textures")
            {
                continue;
            }

            var value = AccountData.Get(pObj, "value");
            if (AccountData.IsString(value))
            {
                try
                {
                    // QByteArray::AbortOnBase64DecodingErrors: invalid input -> null payload
                    texturePayload = Convert.FromBase64String(AccountData.GetString(value));
                }
                catch (FormatException)
                {
                    texturePayload = null;
                }
            }

            if (texturePayload is { Length: > 0 })
            {
                break;
            }
        }

        if (texturePayload is null)
        {
            // qWarning() << "No texture payload data";
            return false;
        }

        var textureObj = TryParseObject(texturePayload);
        if (textureObj is null)
        {
            return false;
        }

        if (AccountData.Get(textureObj, "textures") is not JsonObject textures)
        {
            // qWarning() << "No textures array in response";
            return false;
        }

        var skinOut = new Skin();
        // fill in default skin info ourselves, as this endpoint doesn't provide it
        var steve = IsDefaultModelSteve(output.Id);
        skinOut.Variant = steve ? "CLASSIC" : "SLIM";
        skinOut.Url = steve ? SkinUrlSteve : SkinUrlAlex;
        // sadly we can't figure this out, but I don't think it really matters...
        skinOut.Id = "00000000-0000-0000-0000-000000000000";
        var capeOut = new Cape();
        foreach (var (key, value) in textures)
        {
            if (value is JsonObject textureEntry)
            {
                if (key == "SKIN")
                {
                    if (!GetString(AccountData.Get(textureEntry, "url"), out var skinUrl))
                    {
                        // qWarning() << "Skin url is not a string";
                        return false;
                    }

                    skinOut.Url = AccountData.RewriteTextureUrl(skinUrl);

                    if (AccountData.Get(textureEntry, "metadata") is JsonObject meta)
                    {
                        // might not be present
                        if (GetString(AccountData.Get(meta, "model"), out var model))
                        {
                            skinOut.Variant = model;
                        }
                    }
                }
                else if (key == "CAPE")
                {
                    if (!GetString(AccountData.Get(textureEntry, "url"), out var capeUrl))
                    {
                        // qWarning() << "Cape url is not a string";
                        return false;
                    }

                    capeOut.Url = AccountData.RewriteTextureUrl(capeUrl);

                    // we don't know the cape ID as it is not returned from the session server
                    // so just fake it - changing capes is probably locked anyway :(
                    capeOut.Alias = "cape";
                }
            }
        }

        output.SkinField = skinOut;
        if (capeOut.Alias == "cape")
        {
            output.Capes.Clear();
            output.Capes[capeOut.Alias] = capeOut;
            output.CurrentCape = capeOut.Alias;
        }

        output.Validity = Validity.Certain;
        return true;
    }

    public static bool ParseMinecraftEntitlements(byte[] data, MinecraftEntitlement output)
    {
        var obj = TryParseObject(data);
        if (obj is null)
        {
            return false;
        }

        output.CanPlayMinecraft = false;
        output.OwnsMinecraft = false;

        var itemsArray = AccountData.Get(obj, "items") as JsonArray ?? [];
        foreach (var item in itemsArray)
        {
            if (item is not JsonObject itemObj)
            {
                continue;
            }

            if (!GetString(AccountData.Get(itemObj, "name"), out var itemName))
            {
                continue;
            }

            if (itemName == "game_minecraft")
            {
                output.CanPlayMinecraft = true;
            }

            if (itemName == "product_minecraft")
            {
                output.OwnsMinecraft = true;
            }
        }

        output.Validity = Validity.Certain;
        return true;
    }

    public static bool ParseRolloutResponse(byte[] data, out bool result)
    {
        result = false;
        var obj = TryParseObject(data);
        if (obj is null)
        {
            return false;
        }

        if (!GetString(AccountData.Get(obj, "feature"), out var feature))
        {
            // qWarning() << "Rollout feature is not a string";
            return false;
        }

        if (feature != "msamigration")
        {
            // qWarning() << "Rollout feature is not what we expected (msamigration)";
            return false;
        }

        if (!GetBool(AccountData.Get(obj, "rollout"), out var rollout))
        {
            // qWarning() << "Rollout feature is not a string";
            return false;
        }

        result = rollout;
        return true;
    }

    public static bool ParseMojangResponse(byte[] data, Token output)
    {
        var obj = TryParseObject(data);
        if (obj is null)
        {
            return false;
        }

        if (!GetNumber(AccountData.Get(obj, "expires_in"), out double expiresIn))
        {
            // qWarning() << "expires_in is not a valid number";
            return false;
        }

        var currentTime = DateTime.UtcNow;
        output.IssueInstant = currentTime;
        output.NotAfter = currentTime.AddSeconds(expiresIn);

        if (!GetString(AccountData.Get(obj, "username"), out var _))
        {
            // qWarning() << "username is not valid";
            return false;
        }

        // TODO: it's a JWT... validate it?
        if (!GetString(AccountData.Get(obj, "access_token"), out var accessToken))
        {
            // qWarning() << "access_token is not valid";
            return false;
        }

        output.Value = accessToken;
        output.Validity = Validity.Certain;
        return true;
    }

    /// <summary>QJsonDocument::fromJson + object() equivalent: null when the payload is not a JSON object.</summary>
    private static JsonObject? TryParseObject(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
