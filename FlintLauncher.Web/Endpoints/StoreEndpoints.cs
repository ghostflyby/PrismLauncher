// SPDX-License-Identifier: GPL-3.0-only
// The /store surface: the layered completeness API. The outer
// layer is the manifest-driven derived view (the dpkg --verify model) served here:
//   GET /store/status/libraries/{versionRef} — the meta version manifest's libraries[]
//     checked against the libraries root + the ledger;
//   GET /store/status/assets/{assetsId}     — the asset index objects map checked against
//     the assets root.
// Both are PURE derivations (StoreCompleteness): no downloads, no ledger writes. The inner
// layer (per-file ledger entry queries) is the metacache eviction surface that exists.

using System.Text.Json;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Store;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class StoreEndpoints
{
    public static void MapStore(this RouteGroupBuilder api)
    {
        api.WithTags("Store");
        var group = api.MapGroup("/store");

        group.MapGet("/status/libraries/{versionRef}", (LauncherRuntime runtime, string versionRef) =>
        {
            StoreStatusReport? report;
            try
            {
                report = StoreCompleteness.LibrariesStatus(runtime.Paths, runtime.MetaCache, versionRef,
                    new RuntimeContext());
            }
            catch (Exception e) when (e is MetaParseException or JsonException)
            {
                return ApiJson.Error("invalid_version_manifest",
                    $"the version manifest for '{versionRef}' is not parseable: {e.Message}", 422);
            }

            return report is null
                ? ApiJson.NotFound("version_not_found",
                    $"no meta version manifest for 'net.minecraft/{versionRef}' — the version must be loaded first")
                : Results.Ok(report);
        }).Produces<StoreStatusReport>();

        group.MapGet("/status/assets/{assetsId}", (LauncherRuntime runtime, string assetsId) =>
        {
            var report = StoreCompleteness.AssetsStatus(runtime.Paths, assetsId);
            return report is null
                ? ApiJson.NotFound("index_not_found",
                    $"no asset index '{assetsId}' under the store's indexes — the assets must be installed first")
                : Results.Ok(report);
        }).Produces<StoreStatusReport>();
    }
}
