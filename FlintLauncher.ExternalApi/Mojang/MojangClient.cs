// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Mojang;

/// <summary>The client for the Minecraft account services: the Microsoft identity platform,
/// Xbox Live and Minecraft services (with the skin textures). The groups hand out the
/// endpoint operations; send each with <c>ApiOperation.SendTo</c> through the HttpClient of
/// the caller's choice.</summary>
public sealed class MojangClient(MojangClientOptions? options = null)
{
    /// <summary>The service endpoints and MSA client id the operations read per send; apply
    /// changes through the same instance.</summary>
    public MojangClientOptions Options { get; } = options ?? new MojangClientOptions();

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The Microsoft identity endpoints: device authorization, the token grants and
    /// the interactive authorization URL.</summary>
    public MojangMicrosoftQueries Microsoft => new(this);

    /// <summary>The Xbox Live endpoints: user authentication and XSTS authorization.</summary>
    public MojangXboxQueries Xbox => new(this);

    /// <summary>The Minecraft services endpoints: launcher login, entitlements, the profile
    /// and its skin and cape.</summary>
    public MojangServicesQueries MinecraftServices => new(this);

    /// <summary>The skin texture download a profile points at.</summary>
    public MojangTextureQueries Textures => new();
}
