// SPDX-License-Identifier: GPL-3.0-only
// Tests for the InstanceCopyPrefs/InstanceCopyTask port. Everything synthetic (temp dirs);
// the copy flows go through the real staging machinery of the InstanceList port, like the
// upstream MainWindow::on_actionCopyInstance_triggered flow (dialog -> task -> staging).
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using Xunit;

namespace Flint.Tests;

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
        catch (DirectoryNotFoundException)
        {
        }
    }

    private FlintLauncher.Core.Instances.MinecraftInstance CreateSource() =>
        new(_global, new IniSettingsObject(Path.Combine(_sourceDir, "instance.cfg")), _sourceDir);

    private InstanceList CreateList() => new(_global, [_instancesDir]) { GroupFilePath = _groupFile };

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
        task.SetGroup("Copied");
        task.SetIcon("flint-icon");
        task.SetParentSettings(_global);

        await list.WrapInstanceTask(task).StartAsync();
        Assert.True(task.WasSuccessful);

        var copiedDir = Path.Combine(_instancesDir, "Source Copy");
        Assert.True(Directory.Exists(copiedDir));
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "saves", "World One", "level.dat")));
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "mods", "somemod.jar")));
        Assert.True(File.Exists(Path.Combine(copiedDir, "minecraft", "options.txt")));
        Assert.True(File.Exists(Path.Combine(copiedDir, "instance.cfg")));

        // reload from disk through the list: id = folder name, new uuid, new name/icon
        list.LoadList();
        var copy = list.GetInstanceById("Source Copy");
        Assert.NotNull(copy);
        Assert.Equal("Source Copy", copy!.Name());
        Assert.Equal("flint-icon", copy.IconKey());
        Assert.NotEqual(source.Uuid, copy.Uuid);
        Assert.Equal(32, copy.Uuid.Length);
        Assert.Equal("Copied", list.GetInstanceGroup("Source Copy"));

        // keepPlaytime=true: the copied playtime survives
        var copyCfg = File.ReadAllText(Path.Combine(copiedDir, "instance.cfg"));
        Assert.Contains("totalTimePlayed=1234", copyCfg);
    }

    [Fact]
    public async Task Copy_KeepPlaytimeOff_ResetsPlaytime()
    {
        var source = CreateSource();
        var task = new InstanceCopyTask(source, new InstanceCopyPrefs { KeepPlaytime = false });
        task.SetName("Fresh Copy");
        task.SetParentSettings(_global);

        var list = CreateList();
        await list.WrapInstanceTask(task).StartAsync();
        Assert.True(task.WasSuccessful);

        var copyCfg = File.ReadAllText(Path.Combine(_instancesDir, "Fresh Copy", "instance.cfg"));
        Assert.DoesNotContain("totalTimePlayed", copyCfg);
        Assert.DoesNotContain("lastTimePlayed", copyCfg);
    }

    [Fact]
    public async Task Copy_WithSavesExcluded_OmitsSaves()
    {
        var source = CreateSource();
        var task = new InstanceCopyTask(source, new InstanceCopyPrefs { CopySaves = false });
        task.SetName("No Saves");
        task.SetParentSettings(_global);

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

        // copying without changing the name: the instance.cfg name stays "Source", the first
        // folder is taken, so FS::DirNameFromString appends "(1)"
        var first = new InstanceCopyTask(source, new InstanceCopyPrefs());
        first.SetName("Source");
        first.SetParentSettings(_global);
        await list.WrapInstanceTask(first).StartAsync();
        Assert.True(first.WasSuccessful);

        var second = new InstanceCopyTask(source, new InstanceCopyPrefs());
        second.SetName("Source");
        second.SetParentSettings(_global);
        await list.WrapInstanceTask(second).StartAsync();
        Assert.True(second.WasSuccessful);

        Assert.True(Directory.Exists(Path.Combine(_instancesDir, "Source(1)")));

        list.LoadList();
        var copy = list.GetInstanceById("Source(1)");
        Assert.NotNull(copy);
        Assert.Equal("Source", copy!.Name());
        Assert.NotEqual(source.Uuid, copy.Uuid);
    }

    [Fact]
    public async Task Copy_LinkModes_FailWithClearMessage()
    {
        var source = CreateSource();

        var symlinks = new InstanceCopyTask(source, new InstanceCopyPrefs { UseSymLinks = true });
        symlinks.SetParentSettings(_global);
        await symlinks.StartAsync();
        Assert.Equal(FlintLauncher.Core.Tasks.TaskState.Failed, symlinks.State);

        var clone = new InstanceCopyTask(source, new InstanceCopyPrefs { UseClone = true });
        clone.SetParentSettings(_global);
        await clone.StartAsync();
        Assert.Equal(FlintLauncher.Core.Tasks.TaskState.Failed, clone.State);

        // nothing was copied (only the source instance itself is there)
        Assert.Equal(["Source"], Directory.GetFileSystemEntries(_instancesDir)
            .Select(Path.GetFileName).ToList());
    }
}
