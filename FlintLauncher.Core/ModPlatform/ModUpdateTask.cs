// SPDX-License-Identifier: GPL-3.0-only
// Port of the "apply update" orchestration the C++ drives from the UI layer
// (ModFolderPage::updateMods / downloadDialogFinished + ResourceUpdateDialog::getTasks):
// every checked update is a ResourceDownloadTask built by the check pass
// (`new ResourceDownloadTask(pack, newVersion, model, true, "update")`, see
// ModrinthCheckUpdate.cpp / FlameCheckUpdate.cpp); applying them is a task group over
// those downloads followed by m_model->update().
//
// Deviations:
//  - The C++ uses a ConcurrentTask("Download Mods", NumberOfConcurrentDownloads); here the
//    concurrency is the maxConcurrent constructor parameter, defaulting to 1 (sequential
//    application). Failure semantics are the ConcurrentTask ones: a failing download does
//    not abort its siblings, the model refresh still runs, and the task fails at the end
//    with the failing download's reason.
//  - The review-dialog UX (candidate list, per-resource check state) is the host
//    application's job; this task takes the already-selected download tasks.

using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform;

public class ModUpdateTask : ConcurrentTask
{
    public ModUpdateTask(ResourceFolderModel model, IEnumerable<ResourceDownloadTask> downloads,
        string taskName = "Download Mods", int maxConcurrent = 1)
        : base(taskName, maxConcurrent)
    {
        foreach (var download in downloads)
        {
            AddTask(download);
        }

        // m_model->update() runs after the downloads complete (even when some failed), so
        // the folder model picks up the new/removed files and their metadata.
        AddTask(new RefreshModelTask(model));
    }

    private sealed class RefreshModelTask : FlintTask
    {
        private readonly ResourceFolderModel _model;

        public RefreshModelTask(ResourceFolderModel model)
        {
            _model = model;
        }

        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _model.Update();
            EmitSucceeded();
            return Task.CompletedTask;
        }
    }
}
