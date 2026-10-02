// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/NullInstance.h: the placeholder instance used when an instance folder
// exists but its type cannot be instantiated.
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Instances;

public class NullInstance : BaseInstance
{
    public NullInstance(SettingsObject settings, string rootDir)
        : base(settings, rootDir)
    {
        SetVersionBroken(true);
    }

    public override void SaveNow() { }

    public override void LoadSpecificSettings() => SetSpecificSettingsLoaded(true);

    public override string GetStatusbarDescription() => "Unknown instance type";

    public override IReadOnlySet<string> Traits() => new HashSet<string>();

    public override string InstanceConfigFolder() => InstanceRoot();

    public override Launch.LaunchTask? CreateLaunchTask(AuthSession? account, MinecraftTarget? targetToJoin) => null;

    public override List<FlintTask> CreateUpdateTask() => [];

    public override ProcessEnvironment CreateEnvironment() => new();

    public override ProcessEnvironment CreateLaunchEnvironment() => new();

    public override SortedDictionary<string, string> GetVariables() => [];

    public override IReadOnlyList<string> GetLogFileSearchPaths() => [];

    public override bool CanExport() => false;

    public override bool CanEdit() => false;

    public override bool CanLaunch() => false;

    public override List<string> VerboseDescription(AuthSession? session, MinecraftTarget? targetToJoin)
    {
        return ["Null instance - placeholder."];
    }

    public override string ModsRoot() => "";

    public override void UpdateRuntimeContext()
    {
        // NOOP
    }
}
