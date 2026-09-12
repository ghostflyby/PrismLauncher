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
    private bool _modsDone;
    private bool _coreModsDone;
    private bool _nilModsDone;

    public ScanModFolders(LaunchTask parent) : base(parent)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var inst = Parent.Instance;

        var loaders = inst.LoaderModList();
        _modsDone = loaders.Update();

        var cores = inst.CoreModList();
        _coreModsDone = cores.Update();

        var nils = inst.NilModList();
        _nilModsDone = nils.Update();

        CheckDone();
        return Task.CompletedTask;
    }

    private void CheckDone()
    {
        if (_modsDone && _coreModsDone && _nilModsDone)
        {
            EmitSucceeded();
        }
    }
}
