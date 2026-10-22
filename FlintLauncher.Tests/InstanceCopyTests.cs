// SPDX-License-Identifier: GPL-3.0-only
// Tests for the InstanceCopyPrefs/InstanceCopyTask port. Everything synthetic (temp dirs);
// the copy flows go through the real staging machinery of the InstanceList port, like the
// upstream MainWindow::on_actionCopyInstance_triggered flow (dialog -> task -> staging).
// D4: the source here is Prism format (instance.cfg, read-only); the copy's metadata fixups
// run through the typed adapter, so the staged folder commits as Flint format (instance.json
// + instance-state.json) while the copied cfg stays untouched on disk.

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Tasks;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class InstanceCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-copy-{Guid.NewGuid():N}");
    private readonly string _instancesDir;
    private readonly string _groupFile;
    private readonly string _sourceDir;
    private readonly TestGlobalSettings _global = new();

    public InstanceCopyTests()
    {
        Directory.CreateDirectory(_root);
        _instancesDir = Path.Combine(_root, "instances");
        Directory.CreateDirectory(_instancesDir);
        _groupFile = Path.Combine(_root, "instgroups.json");
        _sourceDir = Path.Combine(_instancesDir, "Source");
        Directory.CreateDirectory(_sourceDir);
        File.WriteAllLines(Path.Combine(_sourceDir, "instance.cfg"),
        [
            "InstanceType=OneSix",
            "name=Source",
            "totalTimePlayed=1234",
        ]);

        // game root content (default "minecraft" layout)
        var gameRoot = Path.Combine(_sourceDir, "minecraft");
        Directory.CreateDirectory(Path.Combine(gameRoot, "saves", "World One"));
        File.WriteAllText(Path.Combine(gameRoot, "saves", "World One", "level.dat"), "level");
        Directory.CreateDirectory(Path.Combine(gameRoot, "mods"));
        File.WriteAllText(Path.Combine(gameRoot, "mods", "somemod.jar"), "mod");
        File.WriteAllText(Path.Combine(gameRoot, "options.txt"), "option=value");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private MinecraftInstance CreateSource() =>
        new(new InstanceJsonSettingsObject(new AbsolutePath(_sourceDir), _global.Environment),
            new AbsolutePath(_sourceDir));

    private InstanceList CreateList() =>
        new([new AbsolutePath(_instancesDir)])
        {
            Environment = _global,
            GroupFilePath = new AbsolutePath(_groupFile),
        };

    // ------------------------------------------------------------------ prefs/filters

    [Fact]
    public void Prefs_Defaults_MatchUpstream()
    {
        var prefs = new InstanceCopyPrefs();
        Assert.True(prefs.AllTrue());
        Assert.True(prefs.CopySaves);
        Assert.True(prefs.KeepPlaytime);
        Assert.True(prefs.CopyGameOptions);
        Assert.True(prefs.CopyResourcePacks);
        Assert.True(prefs.CopyShaderPacks);
        Assert.True(prefs.CopyServers);
        Assert.True(prefs.CopyMods);
        Assert.True(prefs.CopyScreenshots);
        Assert.False(prefs.UseSymLinks);
        Assert.False(prefs.LinkRecursively);
        Assert.False(prefs.UseHardLinks);
        Assert.False(prefs.DontLinkSaves);
        Assert.False(prefs.UseClone);

        // no filters when everything is on
        Assert.Equal("", prefs.GetSelectedFiltersAsRegex());
    }

    [Fact]
    public void Prefs_FiltersAsRegex_MatchUpstreamComposition()
    {
        var prefs = new InstanceCopyPrefs
        {
            CopySaves = false,
            CopyMods = false,
        };
        Assert.False(prefs.AllTrue());
        Assert.Equal("[.]?minecraft/saves|[.]?minecraft/coremods|[.]?minecraft/mods|[.]?minecraft/config",
            prefs.GetSelectedFiltersAsRegex());

        // resource packs disable both folder generations
        var rp = new InstanceCopyPrefs { CopyResourcePacks = false };
        Assert.Equal("[.]?minecraft/resourcepacks|[.]?minecraft/texturepacks", rp.GetSelectedFiltersAsRegex());

        // servers disable the three server artifacts
        var servers = new InstanceCopyPrefs { CopyServers = false };
        Assert.Equal("[.]?minecraft/servers.dat|[.]?minecraft/servers.dat_old|[.]?minecraft/server-resource-packs",
            servers.GetSelectedFiltersAsRegex());

        // additional filters are appended
        var extra = new InstanceCopyPrefs { CopySaves = false };
        Assert.Equal("[.]?minecraft/saves|[.]?minecraft/extra", extra.GetSelectedFiltersAsRegex(["extra"]));
    }

    // --------------------------------------------------------------------- copy flows

    [Fact]
    public async Task Copy_AllPrefsOn_ProducesNewInstanceWithNewUuid()
    {
        var source = CreateSource();
        var list = CreateList();
        // upstream commits can only persist groups after the startup scan probed the list
        Assert.Equal(InstListError.NoError, list.LoadList());

        var prefs = new InstanceCopyPrefs(); // everything on
        var task = new InstanceCopyTask(source, prefs);
        // what CopyInstanceDialog submits: the chosen name/group/icon
        task.SetName("Source Copy");
        task.Group = "Copied";
        task.Icon = "flint-icon";
        task.SetParentEnvironment(_global.Environment);

        await list.WrapInstanceTask(task).StartAsync();
        Assert.True(task.WasSuccessful);

        var copiedDir = Path.Combine(_instancesDir, "Source Copy");
        Assert.True(Directory.Exists(copiedDir));
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "saves", "World One", "level.dat")));
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "mods", "somemod.jar")));
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "options.txt")));
        // the copied source cfg rides along untouched; the copy's OWN layer is instance.json
        Assert.True(File.Exists(Path.Combine(copiedDir, "instance.cfg")));
        Assert.True(File.Exists(Path.Combine(copiedDir, "instance.json")));

        // reload from disk through the list: id = folder name, new uuid, new name/icon
        list.LoadList();
        var copy = list.GetInstanceById("Source Copy");
        Assert.NotNull(copy);
        Assert.Equal("Source Copy", copy.Name());
        Assert.Equal("flint-icon", copy.IconKey());
        Assert.NotEqual(source.Uuid, copy.Uuid);
        Assert.Equal(32, copy.Uuid.Length);
        Assert.Equal("Copied", list.GetInstanceGroup("Source Copy"));

        // keepPlaytime=true: the copied playtime survives (the source's read-only cfg seed
        // was carried into the copy's instance-state.json)
        var copyState = await File.ReadAllTextAsync(Path.Combine(copiedDir, "instance-state.json"));
        Assert.Contains("1234", copyState);
    }

    [Fact]
    public async Task Copy_KeepPlaytimeOff_ResetsPlaytime()
    {
        var source = CreateSource();
        var task = new InstanceCopyTask(source, new InstanceCopyPrefs { KeepPlaytime = false });
        task.SetName("Fresh Copy");
        task.SetParentEnvironment(_global.Environment);

        var list = CreateList();
        await list.WrapInstanceTask(task).StartAsync();
        Assert.True(task.WasSuccessful);

        var copyDir = Path.Combine(_instancesDir, "Fresh Copy");
        // the copied cfg still carries the source's number (read-compat, never rewritten)…
        Assert.Contains("totalTimePlayed=1234",
            await File.ReadAllTextAsync(Path.Combine(copyDir, "instance.cfg")));
        // …but the authoritative state file is zeroed
        var copy = new MinecraftInstance(
            new InstanceJsonSettingsObject(new AbsolutePath(copyDir), _global.Environment),
            new AbsolutePath(copyDir));
        Assert.Equal(0, copy.TotalTimePlayed());
        Assert.Equal(0, copy.LastTimePlayed());
    }

    [Fact]
    public async Task Copy_WithSavesExcluded_OmitsSaves()
    {
        var source = CreateSource();
        var task = new InstanceCopyTask(source, new InstanceCopyPrefs { CopySaves = false });
        task.SetName("No Saves");
        task.SetParentEnvironment(_global.Environment);

        var list = CreateList();
        await list.WrapInstanceTask(task).StartAsync();
        Assert.True(task.WasSuccessful);

        var copiedDir = Path.Combine(_instancesDir, "No Saves");
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "options.txt")));
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "mods", "somemod.jar")));
        Assert.False(Directory.Exists(Path.Combine(copiedDir, "minecraft", "saves")));
    }

    [Fact]
    public async Task Copy_TwiceWithSameName_FolderGetsNumericSuffix()
    {
        var source = CreateSource();
        var list = CreateList();

        // copying without changing the name: the instance layer's name stays "Source", the
        // first folder is taken, so FS::DirNameFromString appends "(1)"
        var first = new InstanceCopyTask(source, new InstanceCopyPrefs());
        first.SetName("Source");
        first.SetParentEnvironment(_global.Environment);
        await list.WrapInstanceTask(first).StartAsync();
        Assert.True(first.WasSuccessful);

        var second = new InstanceCopyTask(source, new InstanceCopyPrefs());
        second.SetName("Source");
        second.SetParentEnvironment(_global.Environment);
        await list.WrapInstanceTask(second).StartAsync();
        Assert.True(second.WasSuccessful);

        Assert.True(Directory.Exists(Path.Combine(_instancesDir, "Source(1)")));

        list.LoadList();
        var copy = list.GetInstanceById("Source(1)");
        Assert.NotNull(copy);
        Assert.Equal("Source", copy.Name());
        Assert.NotEqual(source.Uuid, copy.Uuid);
    }

    [Fact]
    public async Task Copy_LinkModes_FailWithClearMessage()
    {
        var source = CreateSource();

        var symlinks = new InstanceCopyTask(source, new InstanceCopyPrefs { UseSymLinks = true });
        symlinks.SetParentEnvironment(_global.Environment);
        await symlinks.StartAsync();
        Assert.Equal(TaskState.Failed, symlinks.State);

        var clone = new InstanceCopyTask(source, new InstanceCopyPrefs { UseClone = true });
        clone.SetParentEnvironment(_global.Environment);
        await clone.StartAsync();
        Assert.Equal(TaskState.Failed, clone.State);

        // nothing was copied (only the source instance itself is there)
        Assert.Equal(["Source"], Directory.GetFileSystemEntries(_instancesDir)
            .Select(Path.GetFileName).ToList());
    }
}
