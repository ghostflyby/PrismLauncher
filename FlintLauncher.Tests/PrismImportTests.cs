// SPDX-License-Identifier: GPL-3.0-only
// Tests for the Prism→Flint import layer: transfer modes, idempotent resume, the planner
// and the source detector, plus a full PrismImportTask run over a synthetic Prism layout.
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class FileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-xfer-{Guid.NewGuid():N}");

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

    private string WriteSrc(string rel, string content)
    {
        var path = Path.Combine(_root, "src", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string SrcPath(string rel) => Path.Combine(_root, "src", rel);
    private string DstPath(string rel) => Path.Combine(_root, "dst", rel);

    [Theory]
    [InlineData(ImportMode.Move)]
    [InlineData(ImportMode.CopyOnWrite)]
    [InlineData(ImportMode.Copy)]
    public void TransferFile_MovesBytes(ImportMode mode)
    {
        Directory.CreateDirectory(_root);
        var src = WriteSrc("a.txt", "payload");

        Assert.True(FileTransfer.TransferFile(src, DstPath("a.txt"), mode, out var method));

        Assert.Equal("payload", File.ReadAllText(DstPath("a.txt")));
        if (mode == ImportMode.Move)
        {
            Assert.False(File.Exists(src)); // moved: source gone
        }
        else
        {
            Assert.True(File.Exists(src)); // kept
        }
        Assert.NotEqual(TransferMethod.SkippedDestinationExists, method);
    }

    [Fact]
    public void TransferFile_ExistingDestination_IsIdempotentSkip()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "dst"));
        var src = WriteSrc("a.txt", "new");
        var dst = DstPath("a.txt");
        File.WriteAllText(dst, "original");

        Assert.True(FileTransfer.TransferFile(src, dst, ImportMode.Move, out var method));

        Assert.Equal(TransferMethod.SkippedDestinationExists, method);
        Assert.Equal("original", File.ReadAllText(dst)); // not overwritten
    }

    [Fact]
    public void TransferFile_FailingTransfer_KeepsSource()
    {
        Directory.CreateDirectory(_root);
        // a directory at the destination makes File.Copy/move fail
        var src = WriteSrc("a.txt", "x");
        Directory.CreateDirectory(DstPath("a.txt"));

        Assert.False(FileTransfer.TransferFile(src, DstPath("a.txt"), ImportMode.Copy, out _));
        Assert.True(File.Exists(src)); // source intact
    }

    [Fact]
    public void TryClone_RealCloneOnSameVolume()
    {
        Directory.CreateDirectory(_root);
        var src = WriteSrc("clone.txt", "cloned bytes");
        var dst = DstPath("clone.txt");

        Assert.True(FileTransfer.TryClone(src, dst));
        Assert.Equal("cloned bytes", File.ReadAllText(dst));
    }

    [Fact]
    public void TryMoveDirectory_SameVolume_RenamesTree()
    {
        Directory.CreateDirectory(SrcPath("tree/inner"));
        Directory.CreateDirectory(DstPath(""));
        File.WriteAllText(SrcPath("tree/inner/f.txt"), "x");

        Assert.True(FileTransfer.TryMoveDirectory(SrcPath("tree"), DstPath("tree")));

        Assert.True(File.Exists(DstPath("tree/inner/f.txt")));
        Assert.False(Directory.Exists(SrcPath("tree")));
    }

    [Fact]
    public void SameVolume_MatchesSiblings()
    {
        Assert.True(FileTransfer.SameVolume(SrcPath("a"), DstPath("b")));
    }
}

public class PrismImportPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-plan-{Guid.NewGuid():N}");

    public void Dispose() =>
        TryDelete(_root);

    [Fact]
    public void Plan_ListsWhitelistedItemsWithMethods()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "myinst"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "meta"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "cache")); // not whitelisted
        File.WriteAllText(Path.Combine(_root, "src", "accounts.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "src", "instgroups.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "src", "prismlauncher.cfg"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "instances", "myinst", "instance.cfg"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "meta", "index.json"), "x");

        var plan = PrismImportPlanner.Plan(Path.Combine(_root, "src"), Path.Combine(_root, "dst"),
            ImportMode.CopyOnWrite);

        var names = plan.Select(p => p.Name).ToList();
        Assert.Contains("instances", names);
        Assert.Contains("meta", names);
        Assert.Contains("accounts.json", names);
        Assert.Contains("instgroups.json", names);
        Assert.Contains("prismlauncher.cfg", names);
        Assert.DoesNotContain("cache", names);

        var instances = plan.Single(p => p.Name == "instances");
        Assert.Equal(PlannedMethod.PerFile, instances.Method); // CoW mode: per file
        Assert.Equal(1, instances.Files); // instance.cfg only ("myinst" holds no files)
        Assert.Equal(PlannedMethod.PerFile, plan.Single(p => p.Name == "meta").Method); // dst has no meta
    }

    [Fact]
    public void Plan_MoveMode_SameVolume_ItemsPlanRename()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "meta"));
        var plan = PrismImportPlanner.Plan(Path.Combine(_root, "src"), Path.Combine(_root, "dst"),
            ImportMode.Move);
        // src and dst are both under the same temp root → same volume → rename
        Assert.Equal(PlannedMethod.RenameDirectory, plan.Single(p => p.Name == "meta").Method);
    }

    [Fact]
    public void Plan_MissingKeys_AreAbsent()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Assert.Empty(PrismImportPlanner.Plan(Path.Combine(_root, "src"), Path.Combine(_root, "dst"),
            ImportMode.CopyOnWrite));
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public class PrismImportTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-import-task-{Guid.NewGuid():N}");

    public PrismImportTaskTests() => Directory.CreateDirectory(_root);

    public void Dispose() =>
        TryDelete(_root);

    [Fact]
    public async Task ImportTask_MergesWhitelistedContent_AndWritesMarker()
    {
        // synthetic Prism source
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "demo"));
        File.WriteAllText(Path.Combine(_root, "src", "instances", "demo", "instance.cfg"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "accounts.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "src", "meta"));
        File.WriteAllText(Path.Combine(_root, "src", "meta", "index.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "src", "accounts.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "src", "prismlauncher.cfg"), "k=v");
        // pre-existing target content must not be overwritten
        Directory.CreateDirectory(Path.Combine(_root, "dst", "meta"));
        File.WriteAllText(Path.Combine(_root, "dst", "meta", "index.json"), "existing");

        var task = new PrismImportTask(Path.Combine(_root, "src"), Path.Combine(_root, "dst"), ImportMode.CopyOnWrite);
        await task.StartAsync();

        if (!task.WasSuccessful)
        {
            // 探针：把失败细节落到文件（MTP 会截断断言消息）
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "flint-probe-dump.txt"),
                "REASON=" + task.FailReason
                + " | metaExists=" + File.Exists(Path.Combine(_root, "dst", "meta", "index.json"))
                + " | metaContent=" + (File.Exists(Path.Combine(_root, "dst", "meta", "index.json"))
                    ? File.ReadAllText(Path.Combine(_root, "dst", "meta", "index.json"))
                    : "<missing>"));
            throw new Xunit.Sdk.XunitException("see " + Path.Combine(Path.GetTempPath(), "flint-probe-dump.txt"));
        }
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_root, "dst", "meta", "index.json"))); // idempotent skip
        Assert.True(File.Exists(Path.Combine(_root, "dst", "instances", "demo", "instance.cfg")));
        Assert.True(File.Exists(Path.Combine(_root, "dst", "accounts.json")));
        // declined marker written after full success
        Assert.True(File.Exists(Path.Combine(_root, "dst", "prism_nomigrate.txt")));
    }

    [Fact]
    public async Task ImportTask_NoWhitelistedContent_Fails()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var task = new PrismImportTask(Path.Combine(_root, "src"), Path.Combine(_root, "dst"), ImportMode.CopyOnWrite);
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Nothing to import", task.FailReason);
    }


    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public class PrismSourceDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-detect-{Guid.NewGuid():N}");

    public PrismSourceDetectorTests() => Directory.CreateDirectory(_root);

    public void Dispose() =>
        TryDelete(_root);

    [Fact]
    public void Detect_FindsPrismDirWithConfigAndCountsInstances()
    {
        var prism = Path.Combine(_root, "PrismLauncher");
        Directory.CreateDirectory(Path.Combine(prism, "instances", "one"));
        File.WriteAllText(Path.Combine(prism, "prismlauncher.cfg"), "x");

        var candidates = PrismSourceDetector.Detect(_root, [_root]);

        var hit = Assert.Single(candidates, c => c.Path.EndsWith("PrismLauncher"));
        Assert.Equal("prism", hit.LegacyName);
        Assert.True(hit.ConfigFound);
        Assert.Equal(1, hit.InstancesFound);
        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate, hit.Decision);
    }

    [Fact]
    public void Detect_MarkedSource_ReportsMarkerPresent()
    {
        var prism = Path.Combine(_root, "PrismLauncher");
        Directory.CreateDirectory(prism);
        File.WriteAllText(Path.Combine(prism, "prismlauncher.cfg"), "x");
        File.WriteAllText(Path.Combine(_root, "prism_nomigrate.txt"), "");

        var candidates = PrismSourceDetector.Detect(_root, [_root]);

        var hit = Assert.Single(candidates, c => c.Path.EndsWith("PrismLauncher"));
        Assert.True(hit.MarkerPresent);
        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent, hit.Decision);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
