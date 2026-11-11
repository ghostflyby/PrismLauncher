// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors

using FlintLauncher.ExternalApi.Mojang;

namespace FlintLauncher.Core.Auth;

/// <summary>The account service answers applied to the account data.</summary>
internal static class MojangAnswers
{
    /// <summary>An Xbox grant as a token: the display claims land in the extra bag.</summary>
    internal static Token ToToken(this MojangXboxToken grant)
    {
        var token = new Token
        {
            IssueInstant = grant.IssueInstant,
            NotAfter = grant.NotAfter,
            Value = grant.Token,
            Validity = Validity.Certain,
        };
        foreach (var (key, claim) in grant.Claims)
        {
            token.Extra[key] = AccountData.StringElement(claim);
        }

        return token;
    }

    /// <summary>A launcher login onto the Minecraft access token, its lifetime counted from
    /// now.</summary>
    internal static void ApplyTo(this MojangLauncherLogin login, Token output)
    {
        var currentTime = DateTimeOffset.UtcNow;
        output.IssueInstant = currentTime;
        output.NotAfter = currentTime.AddSeconds(login.ExpiresIn);
        output.Value = login.AccessToken;
        output.Validity = Validity.Certain;
    }

    /// <summary>The ownership markers among the entitlement items.</summary>
    internal static void ApplyTo(this MojangEntitlements entitlements, MinecraftEntitlement output)
    {
        output.CanPlayMinecraft = entitlements.ItemNames.Contains("game_minecraft");
        output.OwnsMinecraft = entitlements.ItemNames.Contains("product_minecraft");
        output.Validity = Validity.Certain;
    }

    /// <summary>A profile answer onto the account's profile: a skin stays when the answer
    /// lists no active one, and capes the answer no longer lists stay.</summary>
    internal static void ApplyTo(this MojangProfile profile, MinecraftProfile output)
    {
        output.Id = profile.Id;
        output.Name = profile.Name;
        if (profile.ActiveSkin is { } skin)
        {
            output.SkinField = new Skin
            {
                Id = skin.Id,
                Url = AccountData.RewriteTextureUrl(skin.Url),
                Variant = skin.Variant,
            };
        }

        foreach (var cape in profile.CapeTable.Entries)
        {
            output.Capes[cape.Id] = new Cape
            {
                Id = cape.Id,
                Url = AccountData.RewriteTextureUrl(cape.Url),
                Alias = cape.Alias,
            };
        }

        output.CurrentCape = profile.CapeTable.ActiveCapeId ?? "";
        output.Validity = Validity.Certain;
    }
}
