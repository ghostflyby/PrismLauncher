// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/MinecraftLoadAndCheck.{h,cpp}: load the instance's component
// metadata (resolving it online or offline) before the launch continues.
//
// Deviations from the C++ original:
//  - PackProfile::reload() starts the ComponentUpdateTask and returns; the C++ task then
//    connects to the running task's signals. The C# PackProfile ReloadAsync() already awaits
//    the resolve, so GetCurrentTask() is null again by the time executeTask would check it —
//    the wrapped subtask propagation collapses into the awaited reload (the terminal states
//    map 1:1).
//  - updateLatestMinecraft() (the "UseLatestMinecraftVersion" branch) was deferred in P3 and
//    is not ported, so the whole branch is skipped (documented P5-c follow-up).

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class MinecraftLoadAndCheck : FlintTask
{
    private readonly MinecraftInstance _inst;
    private readonly NetMode _netmode;

    public MinecraftLoadAndCheck(MinecraftInstance inst, NetMode netmode)
    {
        _inst = inst;
        _netmode = netmode;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // add offline metadata load task
        // (the UseLatestMinecraftVersion/updateLatestMinecraft branch is not ported — see header)
        var components = _inst.GetPackProfile();
        var result = await components.ReloadAsync(_netmode, cancellationToken).ConfigureAwait(false);
        if (!result)
        {
            EmitFailed(result.ErrorMessage);
            return;
        }
        EmitSucceeded();
    }

    public bool CanAbort() => true;
}
