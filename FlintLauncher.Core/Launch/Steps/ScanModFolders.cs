// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/ScanModFolders.{h,cpp}: scan the mods/coremods/nilmods
// folders before the launch.
//
// Deviation from the C++ original: the C# ResourceFolderModel::update() is synchronous and
// returns whether it ran, so the three updateFinished signals the C++ waits for collapse into
// three synchronous calls (checkDone() always fires right after).

namespace FlintLauncher.Core.Launch.Steps;

public sealed class ScanModFolders : LaunchStep
{
    private bool modsDone;
    private bool coreModsDone;
    private bool nilModsDone;

    public ScanModFolders(LaunchTask parent) : base(parent) { }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var inst = Parent.Instance;

        var loaders = inst.LoaderModList();
        modsDone = loaders.Update();

        var cores = inst.CoreModList();
        coreModsDone = cores.Update();

        var nils = inst.NilModList();
        nilModsDone = nils.Update();

        CheckDone();
        return Task.CompletedTask;
    }

    private void CheckDone()
    {
        if (modsDone && coreModsDone && nilModsDone)
        {
            EmitSucceeded();
        }
    }
}
