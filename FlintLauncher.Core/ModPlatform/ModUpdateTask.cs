// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Applies the updates a check pass selected: one ResourceDownloadTask per checked update,
// then a refresh so the folder picks up what landed.
//
// The concurrency is a parameter, defaulting to 1 (sequential application). A failing download
// does not abort its siblings and the refresh still runs; the batch throws an
// AggregateException of the failures once every download has had its turn.
//
// The review step (candidate list, per-resource check state) belongs to the caller; this
// takes the already-selected downloads.

using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform;

public static class ModUpdateTask
{
    /// <summary>Applies the updates: the downloads, then the model refresh (which runs even
    /// when some downloads failed, so the folder picks up what did land).</summary>
    public static async Task ApplyAsync(ResourceFolderModel model,
        IEnumerable<ResourceDownloadTask> downloads, CancellationToken cancellationToken,
        int maxConcurrent = 1)
    {
        List<ResourceDownloadTask> units = [.. downloads];
        try
        {
            await TaskWork.RunAllAsync(units,
                    (download, token) => download.RunAsync(token), maxConcurrent, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // the folder still picks up whatever landed, even when a download failed
            model.Update();
        }
    }
}
