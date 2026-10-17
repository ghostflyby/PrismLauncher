// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/VanillaInstanceCreationTask.{h,cpp} (upstream class name:
// VanillaCreationTask): builds the folder layout + instance.cfg + initial mmc-pack.json of
// a fresh vanilla (optionally loader-flavored) instance inside the staging folder.
//
// Deviations:
//  - the C++ task receives BaseVersion::Ptr objects and forwards descriptor() strings; the
//    version-list layer is not ported, so the constructor takes the descriptors directly
//    ("1.20.1", the loader uid, the loader version).
//  - downloadFiles() is not ported: it drives createUpdateTask() (the P5 update pipeline)
//    and a GUI error dialog; creation succeeds without fetching game files.
//  - D4: the staged instance is created through the typed instance.json adapter — new
//    instances are Flint format from birth (no instance.cfg is written).

using FlintLauncher.Core.Config;

namespace FlintLauncher.Core.Instances.Creation;

public sealed class VanillaCreationTask : InstanceTask
{
    private readonly string _version;
    private readonly bool _usingLoader;
    private readonly string _loader = "";
    private readonly string _loaderVersion = "";

    public VanillaCreationTask(string version)
    {
        _version = version;
    }

    public VanillaCreationTask(string version, string loader, string loaderVersion)
    {
        _version = version;
        _usingLoader = true;
        _loader = loader;
        _loaderVersion = loaderVersion;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Creating instance from version " + _version);

        // D4: a fresh Flint instance writes instance.json (the typed adapter) — the staged
        // folder commits as Flint format
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(StagingPath, Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution.")),
            StagingPath);
        {
            var components = instance.GetPackProfile();
            components.BuildingFromScratch();
            // (C++ setComponentVersion("net.minecraft", m_version->descriptor(), true): for a
            // fresh profile this takes the add-new branch, which only appends the component
            // — the important-changed resolve is not started, so no await side effects here)
            await components.SetComponentVersion("net.minecraft", _version, important: true, cancellationToken);
            if (_usingLoader)
            {
                await components.SetComponentVersion(_loader, _loaderVersion);
            }

            instance.SetName(Name());
            instance.SetIconKey(Icon);

            components.SaveNow();
        }

        // (upstream calls downloadFiles(m_instance.get()) here — see the header note)
        EmitSucceeded();
    }
}
