// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/ResourceFolderModel_test.cpp, restricted to the data logic that survives
// without the Qt model layer (the update()/folder scan, install/uninstall, enable toggling
// and the Resource comparators). Fixtures: TestData/ResourcePackParse (upstream
// "testdata/Resources").
//
// Skipped: Qt model only
//  - the EXEC_UPDATE_TASK event-loop/waiting plumbing itself (the ported Update() is
//    synchronous, so there is no updateFinished signal to await);
//  - test_addFromWatch's QFileSystemWatcher behavior (startWatching/stopWatching); the
//    port exercises the same update() folder scan directly.

using FlintLauncher.Core.Minecraft.Resources;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class ResourceFolderModelTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("flint_resource_model_").FullName;

    private string TestDataDir() => Path.Combine(AppContext.BaseDirectory, "TestData", "ResourcePackParse");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { }
    }

    private string NewTempDir(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // test for GH-1178 - install a folder with files to a mod list
    [Fact]
    public void Test_1178()
    {
        // source
        var source = Path.Combine(TestDataDir(), "test_folder");

        // sanity check
        Assert.False(source.EndsWith('/'));

        void Verify(string path)
        {
            var targetDir = Path.Combine(path, "test_folder");
            var entries = Directory.GetFileSystemEntries(targetDir).Select(Path.GetFileName).ToHashSet();
            Assert.Contains("pack.mcmeta", entries);
            Assert.Contains("assets", entries);
        }

        // 1. test with no trailing /
        {
            var folder = source;
            var tempDir = NewTempDir("gh1178_a");

            var m = new ModFolderModel(new AbsolutePath(tempDir), isIndexed: false, createDir: true);

            Assert.True(m.InstallResource(folder));

            Verify(tempDir);
        }

        // 2. test with trailing /
        {
            var folder = source + '/';
            var tempDir = NewTempDir("gh1178_b");

            var m = new ModFolderModel(new AbsolutePath(tempDir), isIndexed: false, createDir: true);

            Assert.True(m.InstallResource(folder));

            Verify(tempDir);
        }
    }

    [Fact]
    public void Test_addFromWatch()
    {
        var source = TestDataDir();
        var model = new ModFolderModel(new AbsolutePath(source), isIndexed: false, createDir: true);

        Assert.Equal(0, model.Size);

        // startWatching() only schedules an update() on the C++ side; run it directly.
        model.Update();

        Assert.Equal(4, model.Size);
    }

    [Fact]
    public void Test_removeResource()
    {
        var folderResource = Path.Combine(TestDataDir(), "test_folder");
        var fileMod = Path.Combine(TestDataDir(), "supercoolmod.jar");

        var tmp = NewTempDir("remove_resource");
        var model = new ResourceFolderModel(new AbsolutePath(tmp), isIndexed: false, createDir: false);

        Assert.Equal(0, model.Size);

        Assert.True(model.InstallResource(fileMod));

        Assert.Equal(1, model.Size);

        // startWatching() on the C++ side; a plain update keeps the same single resource.
        model.Update();

        Assert.Equal(1, model.Size);

        Assert.True(model.InstallResource(folderResource));

        Assert.Equal(2, model.Size);

        Assert.True(model.UninstallResource("supercoolmod.jar"));

        Assert.Equal(1, model.Size);

        var modFileName = model.At(0).Fileinfo.Name;
        Assert.False(modFileName.Length == 0);

        Assert.True(model.UninstallResource(modFileName));

        Assert.Equal(0, model.Size);
    }

    [Fact]
    public void Test_enable_disable()
    {
        var folderResource = Path.Combine(TestDataDir(), "test_folder");
        var fileMod = Path.Combine(TestDataDir(), "supercoolmod.jar");

        var tmp = NewTempDir("enable_disable");
        var model = new ResourceFolderModel(new AbsolutePath(tmp), isIndexed: false, createDir: false);

        Assert.Equal(0, model.Size);

        Assert.True(model.InstallResource(folderResource));
        Assert.True(model.InstallResource(fileMod));

        Assert.Equal(2, model.Size);

        var res1 = model.At(0).Type != ResourceType.Folder ? model.At(0) : model.At(1);
        var res2 = model.At(0).Type == ResourceType.Folder ? model.At(0) : model.At(1);
        var id1 = res1.InternalId;
        var id2 = res2.InternalId;
        var initialEnabledRes2 = res2.Enabled;
        var initialEnabledRes1 = res1.Enabled;

        Assert.True(res1.Type != ResourceType.Folder && res1.Type != ResourceType.Unknown);
        Assert.True(res1.Enabled);

        Assert.True(res1.Enable(EnableAction.Toggle));

        Assert.Equal(!initialEnabledRes1, res1.Enabled);

        Assert.True(res1.Enable(EnableAction.Toggle));

        Assert.Equal(initialEnabledRes1, res1.Enabled);
        Assert.Equal(id1, res1.InternalId);

        Assert.False(res2.Enable(initialEnabledRes2 ? EnableAction.Enable : EnableAction.Disable));
        Assert.Equal(initialEnabledRes2, res2.Enabled);
        Assert.Equal(id2, res2.InternalId);
    }

    /// <summary>
    /// Exercises the ported Resource/Mod sorting comparators (the logic the C++
    /// ResourceFolderModel::ProxyModel::lessThan delegates to).
    /// </summary>
    [Fact]
    public void Test_Comparators()
    {
        var tmp = NewTempDir("comparators");
        var smallPath = Path.Combine(tmp, "alpha.jar");
        var bigPath = Path.Combine(tmp, "the beta.jar"); // "the " prefix is ignored by the name comparator
        File.WriteAllBytes(smallPath, [1, 2, 3]);
        File.WriteAllBytes(bigPath, [1, 2, 3, 4, 5, 6, 7, 8]);
        // make the timestamps distinguishable
        var old = DateTime.Now.AddHours(-2);
        File.SetLastWriteTime(smallPath, old);
        File.SetLastWriteTime(bigPath, DateTime.Now.AddHours(-1));

        var small = new Resource(smallPath);
        var big = new Resource(bigPath);

        Assert.True(small.Compare(big) < 0); // "the beta" -> "beta" > "alpha"
        Assert.True(big.Compare(small) > 0);
        Assert.True(big.Compare(small, SortType.Date) > 0);     // modified later
        Assert.True(big.Compare(small, SortType.Size) > 0);     // larger
        Assert.True(big.Compare(small, SortType.Filename) > 0); // "the beta.jar" vs "alpha.jar"
        Assert.Equal(0, small.Compare(small));

        // disabled resources sort after enabled ones
        Assert.True(small.Enable(EnableAction.Toggle));
        Assert.True(small.Compare(big, SortType.Enabled) < 0);
        Assert.True(small.Enable(EnableAction.Toggle)); // back to enabled

        // Mod version comparator (McVersion-based)
        var modA = new Mod(new AbsolutePath(Path.Combine(tmp, "moda.jar")));
        var modB = new Mod(new AbsolutePath(Path.Combine(tmp, "modb.jar")));
        modA.SetDetails(new ModDetails { ModId = "a", Version = "1.0.0" });
        modB.SetDetails(new ModDetails { ModId = "b", Version = "1.2.0" });
        Assert.True(modA.Compare(modB, SortType.Version) < 0);
        Assert.True(modB.Compare(modA, SortType.Version) > 0);
        // two mods without resolved details share the empty version string, so the version key ties
        var unresolvedA = new Mod(new AbsolutePath(Path.Combine(tmp, "modc.jar")));
        var unresolvedB = new Mod(new AbsolutePath(Path.Combine(tmp, "modd.jar")));
        Assert.Equal(0, unresolvedA.Compare(unresolvedB, SortType.Version));
    }
}
