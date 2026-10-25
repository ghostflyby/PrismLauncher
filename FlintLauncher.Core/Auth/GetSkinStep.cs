// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/GetSkinStep.{h,cpp}.
//
// NOTE: upstream always reports success ("Got skin"), even when the download failed.
//
// Deviation: QUrl accepts an arbitrary (even empty) URL string while System.Uri throws on
// unparseable input; an invalid/empty skin URL therefore takes the failure branch directly
// (still ending in the upstream Working/"Got skin" report).

namespace FlintLauncher.Core.Auth;

public sealed class GetSkinStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Getting skin.";

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(Data.MinecraftProfile.SkinField.Url, UriKind.Absolute, out var url))
        {
            var response = await AuthStepHttp.SendAsync(new AuthStepRequest
            {
                Url = url,
                AutoRetry = true,
            }, _auth.Http, _auth.Settings, cancellationToken).ConfigureAwait(false);
            if (response.Success)
            {
                Data.MinecraftProfile.SkinField.Data = response.Body;
            }
        }

        EmitFinished(AccountTaskState.Working, "Got skin");
    }
}
