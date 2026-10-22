// SPDX-License-Identifier: GPL-3.0-only
// Tests for the Prism→Flint import layer: transfer modes, idempotent resume, the planner
// and the source detector, plus a full PrismImportTask run over a synthetic Prism layout.

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Web;
using TruePath;
using Xunit;
using Xunit.Sdk;

namespace FlintLauncher.Tests;

/// <summary>Whether this environment can create symbolic links at all (Windows without the
/// privilege cannot — the link-dependent tests bow out there instead of failing).</summary>
internal static class SymlinkSupport
{
    public static bool CanCreateLinks()
    {
        var probe = Path.Combine(Path.GetTempPath(), $"flint-link-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "x");
            File.CreateSymbolicLink(probe + ".lnk", probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe + ".lnk"))
                {
                    File.Delete(probe + ".lnk");
                }

                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (IOException) { }
        }
    }
}

public class FileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-xfer-{Guid.NewGuid():N}");

    public void Dispose()
    {
        FileTransfer.CloneAttemptHook = null; // never leak the seam into another test
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string WriteSrc(string rel, string content)
    {
        var path = Path.Combine(_root, "src", rel);
        var pathDir = Path.GetDirectoryName(path);
        Assert.NotNull(pathDir);
        Directory.CreateDirectory(pathDir);
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

    // B1: on volumes without reflink support (ext4, Windows, cross-volume) TryClone fails
    // for every file — the CoW branch must degrade to a plain copy like the Move branch
    // does, or the whole import task EmitFailed's file by file
    [Fact]
    public void TransferFile_CopyOnWrite_FallsBackToPlainCopy_WhenReflinkUnavailable()
    {
        Directory.CreateDirectory(_root);
        FileTransfer.CloneAttemptHook = (_, _) => false; // simulate a non-CoW volume
        var src = WriteSrc("a.txt", "payload");

        Assert.True(FileTransfer.TransferFile(src, DstPath("a.txt"), ImportMode.CopyOnWrite,
            out var method));

        Assert.Equal("payload", File.ReadAllText(DstPath("a.txt")));
        Assert.Equal(TransferMethod.Copy, method); // not a false, and not a fake Reflink
        Assert.True(File.Exists(src));             // CoW keeps the source
    }

    // B5: the transferred file must carry the source's permission bits — accounts.json is
    // 0600 in a Prism data directory and must not land world-readable
    [Fact]
    public void TransferFile_CarriesTheSourcePermissionBits()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no Unix permission bits on Windows
        }

        Directory.CreateDirectory(_root);
        var src = WriteSrc("accounts.json", "{}");
        File.SetUnixFileMode(src, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // every path that lands bytes: reflink (macOS clonefile / Linux FICLONE+carry),
        // plain copy, and the CoW fallback's copy
        Assert.True(FileTransfer.TransferFile(src, DstPath("cow.json"), ImportMode.CopyOnWrite, out _));
        Assert.True(FileTransfer.TransferFile(src, DstPath("copy.json"), ImportMode.Copy, out _));
        FileTransfer.CloneAttemptHook = (_, _) => false;
        Assert.True(FileTransfer.TransferFile(src, DstPath("fallback.json"), ImportMode.CopyOnWrite, out _));

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(DstPath("cow.json")));
        Assert.Equal(expected, File.GetUnixFileMode(DstPath("copy.json")));
        Assert.Equal(expected, File.GetUnixFileMode(DstPath("fallback.json")));
    }

    // B5: the mode-carry helper itself — the exact call the Linux FICLONE branch makes
    // after a successful ioctl (not otherwise reachable on this OS)
    [Fact]
    public void CarryUnixFileMode_SetsTheDestinationModeFromTheSource()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(DstPath(""));
        var src = WriteSrc("secret.txt", "s");
        var dst = DstPath("secret.txt");
        File.WriteAllText(dst, "stale");
        File.SetUnixFileMode(src, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(dst, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                                  | UnixFileMode.OtherRead);

        FileTransfer.CarryUnixFileMode(src, dst);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dst));
    }

    // B4: the migration enumeration — file symlinks are yielded (the C++ ResolveSymlinks
    // walk copies them by content), directory links are never descended into, link cycles
    // cannot spin the walk
    [Fact]
    public void EnumerateFilesDeep_YieldsFileLinksAndHiddenFiles_NeverDescendsDirectoryLinks()
    {
        if (!SymlinkSupport.CanCreateLinks())
        {
            return; // Windows without symlink privilege
        }

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(SrcPath("real/loopA"));
        Directory.CreateDirectory(SrcPath("real/loopB"));
        WriteSrc("real/loopA/inner.txt", "m");
        WriteSrc("real/.hidden", "h");
        File.CreateSymbolicLink(SrcPath("real/link.txt"), SrcPath("real/loopA/inner.txt"));
        // a directory link, and a directory-link CYCLE the walk must survive
        Directory.CreateSymbolicLink(SrcPath("real/dirlink"), SrcPath("real/loopA"));
        Directory.CreateSymbolicLink(SrcPath("real/loopA/intoB"), SrcPath("real/loopB"));
        Directory.CreateSymbolicLink(SrcPath("real/loopB/intoA"), SrcPath("real/loopA"));

        var files = FileTransfer.EnumerateFilesDeep(new AbsolutePath(SrcPath("real")))
            .Select(f => Path.GetRelativePath(SrcPath("real"), f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();

        // no dirlink/*, no loopB/*, no cycle runaway — exactly the real files + the file link
        Assert.Equal([".hidden", "link.txt", "loopA/inner.txt"], files);
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

        var plan = PrismImportPlanner.Plan(new AbsolutePath(Path.Combine(_root, "src")), new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite);

        var names = plan.Select(p => p.Name).ToList();
        Assert.Contains("instances", names);
        Assert.Contains("meta", names);
        Assert.Contains("accounts.json", names);
        Assert.Contains("instgroups.json", names);
        Assert.Contains("prismlauncher.cfg", names);
        Assert.DoesNotContain("cache", names);

        var instances = plan.Single(p => p.Name == "instances");
        Assert.Equal(PlannedMethod.PerFile, instances.Method);                          // CoW mode: per file
        Assert.Equal(1, instances.Files);                                               // instance.cfg only ("myinst" holds no files)
        Assert.Equal(PlannedMethod.PerFile, plan.Single(p => p.Name == "meta").Method); // dst has no meta
    }

    [Fact]
    public void Plan_MoveMode_SameVolume_ItemsPlanRename()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "meta"));
        var plan = PrismImportPlanner.Plan(new AbsolutePath(Path.Combine(_root, "src")), new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.Move);
        // src and dst are both under the same temp root → same volume → rename
        Assert.Equal(PlannedMethod.RenameDirectory, plan.Single(p => p.Name == "meta").Method);
    }

    [Fact]
    public void Plan_MissingKeys_AreAbsent()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Assert.Empty(PrismImportPlanner.Plan(new AbsolutePath(Path.Combine(_root, "src")), new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite));
    }

    // A1/A2 (CONFIG.md:23): the whitelist carries BOTH MultiMC config names — the upstream
    // multimc.cfg (C++ Application.cpp:604) plus the historical mmc.cfg
    [Fact]
    public void ImportFiles_Carry_The_MultiMC_Dual_Name()
    {
        Assert.Contains("multimc.cfg", PrismImportPlanner.ImportFiles);
        Assert.Contains("mmc.cfg", PrismImportPlanner.ImportFiles);
    }

    // B2: Flint startup ALWAYS creates <data>/instances — a destination directory must not
    // swallow the whole item: the plan drills down into the per-file merge
    [Fact]
    public void Plan_DestinationDirectoryExists_PlansTheMergeWhenFilesAreMissing()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "demo"));
        File.WriteAllText(Path.Combine(_root, "src", "instances", "demo", "instance.cfg"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "dst", "instances")); // what Flint startup creates

        var plan = PrismImportPlanner.Plan(new AbsolutePath(Path.Combine(_root, "src")), new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite);

        var instances = plan.Single(p => p.Name == "instances");
        Assert.Equal(PlannedMethod.PerFile, instances.Method); // NOT DestinationExists
        Assert.Equal(1, instances.Files);                      // the source basis — the walk visits exactly these
    }

    // B2: a destination that already holds every source file stays an honest skip
    [Fact]
    public void Plan_DestinationDirectoryExists_StillReportsSkipWhenFullyMerged()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "meta", "net.minecraft"));
        File.WriteAllText(Path.Combine(_root, "src", "meta", "net.minecraft", "index.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "dst", "meta", "net.minecraft"));
        File.WriteAllText(Path.Combine(_root, "dst", "meta", "net.minecraft", "index.json"), "{}");

        var plan = PrismImportPlanner.Plan(new AbsolutePath(Path.Combine(_root, "src")), new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite);

        Assert.Equal(PlannedMethod.DestinationExists, plan.Single(p => p.Name == "meta").Method);
    }

    // B4: the planned file count covers file symlinks (they transfer by content) while
    // directory links add nothing — plan and walk can never disagree
    [Fact]
    public void Plan_CountsFileSymlinks_WhileNotDescendingDirectoryLinks()
    {
        if (!SymlinkSupport.CanCreateLinks())
        {
            return; // Windows without symlink privilege
        }

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "real"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "other"));
        File.WriteAllText(Path.Combine(_root, "src", "instances", "real", "marker.txt"), "m");
        File.WriteAllText(Path.Combine(_root, "src", "instances", "other", "unrelated.txt"), "u");
        File.CreateSymbolicLink(Path.Combine(_root, "src", "instances", "real", "link.txt"),
            Path.Combine(_root, "src", "instances", "real", "marker.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "src", "instances", "dirlink"),
            Path.Combine(_root, "src", "instances", "other"));

        var plan = PrismImportPlanner.Plan(new AbsolutePath(Path.Combine(_root, "src")), new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite);

        Assert.Equal(3, plan.Single(p => p.Name == "instances").Files); // marker + link + unrelated
    }

    // A1: a source carrying only the upstream multimc.cfg plans its import
    [Fact]
    public void Plan_Imports_The_Upstream_MultimcCfg()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "multimc.cfg"), "x");

        var item = Assert.Single(PrismImportPlanner.Plan(new AbsolutePath(Path.Combine(_root, "src")),
            new AbsolutePath(Path.Combine(_root, "dst")), ImportMode.CopyOnWrite));

        Assert.Equal("multimc.cfg", item.Name);
        Assert.False(item.IsDirectory);
        Assert.Equal(1, item.Files);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }
}

public class PrismImportTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-import-task-{Guid.NewGuid():N}");

    public PrismImportTaskTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        FileTransfer.CloneAttemptHook = null; // never leak the seam into another test
        TryDelete(_root);
    }

    [Fact]
    public async Task ImportTask_MergesWhitelistedContent_AndWritesImportedRecord()
    {
        // synthetic Prism source
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "demo"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "instances", "demo", "instance.cfg"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "accounts.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "src", "meta"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "meta", "index.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "accounts.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "prismlauncher.cfg"), "k=v");
        // pre-existing target content must not be overwritten
        Directory.CreateDirectory(Path.Combine(_root, "dst", "meta"));
        await File.WriteAllTextAsync(Path.Combine(_root, "dst", "meta", "index.json"), "existing");

        var decisions = new MigrationDecisionStore(new AbsolutePath(Path.Combine(_root, "state", "migration")),
            FlintJsonContext.Default.MigrationDecisionRecord);
        // S7: the task files under the ACTUAL source name — a polymc source must not be
        // recorded as "prism" (the old hardcode)
        var task = new PrismImportTask(new AbsolutePath(Path.Combine(_root, "src")),
            new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite, "polymc", decisions);
        await task.StartAsync();

        if (!task.WasSuccessful)
        {
            // 探针：把失败细节落到文件（MTP 会截断断言消息）
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "flint-probe-dump.txt"),
                "REASON=" + task.FailReason
                          + " | metaExists=" + File.Exists(Path.Combine(_root, "dst", "meta", "index.json"))
                          + " | metaContent=" + (File.Exists(Path.Combine(_root, "dst", "meta", "index.json"))
                              ? await File.ReadAllTextAsync(Path.Combine(_root, "dst", "meta", "index.json"))
                              : "<missing>"));
            throw new XunitException("see " + Path.Combine(Path.GetTempPath(), "flint-probe-dump.txt"));
        }

        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_root, "dst", "meta", "index.json"))); // idempotent skip
        Assert.True(File.Exists(Path.Combine(_root, "dst", "instances", "demo", "instance.cfg")));
        Assert.True(File.Exists(Path.Combine(_root, "dst", "accounts.json")));

        // S3: an `imported` decision record after full success — no marker file anywhere
        Assert.False(File.Exists(Path.Combine(_root, "dst", "prism_nomigrate.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "dst", "polymc_nomigrate.txt")));
        var record = decisions.Read("polymc");
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Imported, record.Decision);
        Assert.Equal("cow", record.Mode);
        Assert.NotEqual(MigrationDecisionRecord.UnknownAt, record.At);
        Assert.Equal(1, record.Instances); // the demo instance dir at the source
        Assert.True(record.Bytes > 0);
    }

    // B1 at the task level: on a volume without reflink support the CoW import used to
    // fail EVERY file and EmitFailed the whole task — the copy fallback must carry it
    [Fact]
    public async Task ImportTask_OnReflinklessVolume_StillSucceedsThroughTheCopyFallback()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "demo"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "instances", "demo", "instance.cfg"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "accounts.json"), "{}");
        FileTransfer.CloneAttemptHook = (_, _) => false; // simulate ext4/Windows/cross-volume

        var task = new PrismImportTask(new AbsolutePath(Path.Combine(_root, "src")),
            new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.True(File.Exists(Path.Combine(_root, "dst", "instances", "demo", "instance.cfg")));
        Assert.True(File.Exists(Path.Combine(_root, "dst", "accounts.json")));
        Assert.True(File.Exists(Path.Combine(_root, "src", "accounts.json"))); // CoW keeps the source
    }

    [Fact]
    public async Task ImportTask_NoWhitelistedContent_Fails()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var task = new PrismImportTask(new AbsolutePath(Path.Combine(_root, "src")),
            new AbsolutePath(Path.Combine(_root, "dst")), ImportMode.CopyOnWrite);
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Nothing to import", task.FailReason);
    }

    // B2 production scenario: Flint startup ALWAYS creates <data>/instances before any
    // import can run — the import must merge the instance tree into the existing
    // directory and succeed, not skip the whole item behind an imported record
    [Fact]
    public async Task ImportTask_PreExistingInstancesDirectory_MergesTheTree_AndSucceeds()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "demo"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "instances", "demo", "instance.cfg"),
            "InstanceType=OneSix\nname=demo\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "prismlauncher.cfg"), "k=v");
        Directory.CreateDirectory(Path.Combine(_root, "dst", "instances")); // Flint startup's own creation
        // pre-existing sibling content must survive the merge untouched
        Directory.CreateDirectory(Path.Combine(_root, "dst", "instances", "native"));
        await File.WriteAllTextAsync(Path.Combine(_root, "dst", "instances", "native", "instance.cfg"), "native");

        var decisions = new MigrationDecisionStore(new AbsolutePath(Path.Combine(_root, "state", "migration")),
            FlintJsonContext.Default.MigrationDecisionRecord);
        var task = new PrismImportTask(new AbsolutePath(Path.Combine(_root, "src")),
            new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.CopyOnWrite, "prism", decisions);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.True(File.Exists(Path.Combine(_root, "dst", "instances", "demo", "instance.cfg")));
        Assert.Equal("native", await File.ReadAllTextAsync(
            Path.Combine(_root, "dst", "instances", "native", "instance.cfg"))); // not clobbered

        // and the success is honest: the record lands only WITH the tree actually merged
        var record = decisions.Read("prism");
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Imported, record.Decision);
        Assert.Equal(1, record.Instances);
    }

    [Fact]
    public async Task ImportTask_MoveMode_RecordsInstanceCount_FromBeforeTheTransfer()
    {
        // a same-volume move renames <src>/instances away wholesale — the record's instance
        // count must come from the pre-transfer inventory, not from the emptied source
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "demo"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "instances", "second"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "instances", "demo", "instance.cfg"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "prismlauncher.cfg"), "k=v");

        var decisions = new MigrationDecisionStore(new AbsolutePath(Path.Combine(_root, "state-move", "migration")),
            FlintJsonContext.Default.MigrationDecisionRecord);
        var task = new PrismImportTask(new AbsolutePath(Path.Combine(_root, "src")),
            new AbsolutePath(Path.Combine(_root, "dst")),
            ImportMode.Move, "prism", decisions);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.False(Directory.Exists(Path.Combine(_root, "src", "instances"))); // the wholesale move happened
        var record = decisions.Read("prism");
        Assert.NotNull(record);
        Assert.Equal("move", record.Mode);
        Assert.Equal(2, record.Instances);
    }


    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
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

        var candidates = PrismSourceDetector.Detect(new AbsolutePath(_root), [new AbsolutePath(_root)]);

        var hit = Assert.Single(candidates, c => c.Path.Value.EndsWith("PrismLauncher"));
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

        var candidates = PrismSourceDetector.Detect(new AbsolutePath(_root), [new AbsolutePath(_root)]);

        var hit = Assert.Single(candidates, c => c.Path.Value.EndsWith("PrismLauncher"));
        Assert.True(hit.MarkerPresent);
        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent, hit.Decision);
    }

    // A1: upstream MultiMC writes multimc.cfg (C++ Application.cpp:604; the C++ tree
    // carries no mmc.cfg) — the upstream name alone must surface a MultiMC source, which
    // the historical mmc.cfg-only probe never did
    [Fact]
    public void Detect_MultiMC_Is_Found_Via_The_Upstream_MultimcCfg()
    {
        var multimc = Path.Combine(_root, PrismSourceDetector.NameToDirectory("multimc"));
        Directory.CreateDirectory(multimc);
        File.WriteAllText(Path.Combine(multimc, "multimc.cfg"), "x");

        var candidates = PrismSourceDetector.Detect(new AbsolutePath(_root), [new AbsolutePath(_root)]);

        var hit = Assert.Single(candidates);
        Assert.Equal("multimc", hit.LegacyName);
        Assert.Equal("multimc.cfg", hit.ConfigFile);
        Assert.True(hit.ConfigFound);
        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate, hit.Decision);
    }

    // A1 (CONFIG.md:23 dual-name): the historical mmc.cfg stays probed as the fallback name
    [Fact]
    public void Detect_MultiMC_Is_Still_Found_Via_The_Historical_MmcCfg()
    {
        var multimc = Path.Combine(_root, PrismSourceDetector.NameToDirectory("multimc"));
        Directory.CreateDirectory(multimc);
        File.WriteAllText(Path.Combine(multimc, "mmc.cfg"), "x");

        var candidates = PrismSourceDetector.Detect(new AbsolutePath(_root), [new AbsolutePath(_root)]);

        var hit = Assert.Single(candidates);
        Assert.Equal("multimc", hit.LegacyName);
        Assert.Equal("mmc.cfg", hit.ConfigFile);
    }

    // A1: both names in one directory → ONE candidate, and the upstream multimc.cfg wins
    [Fact]
    public void Detect_MultiMC_With_Both_Config_Names_Is_One_Candidate_Preferring_Upstream()
    {
        var multimc = Path.Combine(_root, PrismSourceDetector.NameToDirectory("multimc"));
        Directory.CreateDirectory(multimc);
        File.WriteAllText(Path.Combine(multimc, "multimc.cfg"), "x");
        File.WriteAllText(Path.Combine(multimc, "mmc.cfg"), "x");

        var hit = Assert.Single(PrismSourceDetector.Detect(new AbsolutePath(_root), [new AbsolutePath(_root)]));

        Assert.Equal("multimc", hit.LegacyName);
        Assert.Equal("multimc.cfg", hit.ConfigFile);
    }

    // A1: the MultiMC probe path follows the C++ shape (Application.cpp:603
    // AppDataLocation/../../multimc): LOWERCASE on Linux (where DefaultParents scans
    // ~/.local/share), the installed directory's capitalized name elsewhere
    [Fact]
    public void NameToDirectory_MultiMC_Follows_The_Cpp_Probe_Path()
    {
        Assert.Equal(OperatingSystem.IsLinux() ? "multimc" : "MultiMC",
            PrismSourceDetector.NameToDirectory("multimc"));
        Assert.Equal("PolyMC", PrismSourceDetector.NameToDirectory("polymc"));
        Assert.Equal("PrismLauncher", PrismSourceDetector.NameToDirectory("prism"));
    }

    // A3: the existing-data decision runs against the CONFIG root's flint.json (CONFIG C5)
    // — the pre-reform <data>/flint.cfg position is dead, so the C++ protection
    // (Application.cpp:1994-1999) must fire on the authoritative position again
    [Fact]
    public void Detect_Existing_Target_Config_Reports_CurrentDataExists_Via_The_Config_Root()
    {
        var prism = Path.Combine(_root, "PrismLauncher");
        Directory.CreateDirectory(prism);
        File.WriteAllText(Path.Combine(prism, "prismlauncher.cfg"), "x");
        var configRoot = Path.Combine(_root, "config");
        Directory.CreateDirectory(configRoot);
        var flintJson = Path.Combine(configRoot, "flint.json");

        // no flint.json yet → ready to migrate
        var before = Assert.Single(PrismSourceDetector.Detect(new AbsolutePath(_root), [new AbsolutePath(_root)], null, new AbsolutePath(flintJson)));
        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate, before.Decision);
        Assert.False(before.TargetHasOwnConfig);

        // Flint's own config appears → the C++ existing-data protection fires
        File.WriteAllText(flintJson, "{}");
        var hit = Assert.Single(PrismSourceDetector.Detect(new AbsolutePath(_root), [new AbsolutePath(_root)], null, new AbsolutePath(flintJson)));
        Assert.Equal(DataMigration.MigrationDecision.CurrentDataExists, hit.Decision);
        Assert.True(hit.TargetHasOwnConfig);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }
}
