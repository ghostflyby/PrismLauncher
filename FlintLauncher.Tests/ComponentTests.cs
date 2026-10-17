// SPDX-License-Identifier: GPL-3.0-only
// New tests locking the Component behavior ported from launcher/minecraft/Component.{h,cpp}:
// the enabled/important/removable flag matrix, the UpdateAction variant plumbing, problem
// aggregation and the customize/revert (local override file) flow.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Minecraft;
using Xunit;

namespace FlintLauncher.Tests;

public class ComponentTests : IDisposable
{
    private readonly string _root;

    public ComponentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flint-component-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static VersionFile MakeVersionFile(string uid, string version, string name)
    {
        return new VersionFile
        {
            Name = name,
            Uid = uid,
            Version = version,
        };
    }

    [Fact]
    public void EnableDisableFlagMatrix()
    {
        using var profile = new PackProfile(_root);
        var normal = new Component(profile, "test.normal");
        Assert.True(normal.IsEnabled());
        Assert.True(normal.CanBeDisabled());
        Assert.True(normal.SetEnabled(false));
        Assert.False(normal.IsEnabled());
        // setting the same state again reports no change
        Assert.False(normal.SetEnabled(false));

        // important components cannot be disabled
        var important = new Component(profile, "test.important");
        important.SetImportant(true);
        Assert.False(important.CanBeDisabled());
        Assert.True(important.IsEnabled());
        Assert.False(important.SetEnabled(false));
        Assert.True(important.IsEnabled());

        // dependency-only components cannot be disabled either
        var dep = new Component(profile, "test.dep") { DependencyOnly = true };
        Assert.False(dep.CanBeDisabled());
        Assert.False(dep.SetEnabled(false));
        Assert.True(dep.IsEnabled());

        Assert.True(normal.IsRemovable());
        Assert.False(important.IsRemovable());
        Assert.True(normal.IsMoveable());
    }

    [Fact]
    public void KnownModloaders()
    {
        using var profile = new PackProfile(_root);
        var fabric = new Component(profile, "net.fabricmc.fabric-loader");
        Assert.True(fabric.IsKnownModloader());
        Assert.Contains("net.minecraftforge", fabric.KnownConflictingComponents());

        var plain = new Component(profile, "test.something");
        Assert.False(plain.IsKnownModloader());
        Assert.Empty(plain.KnownConflictingComponents());
    }

    [Fact]
    public void UpdateCachedDataCopiesRequires()
    {
        using var profile = new PackProfile(_root);
        var component = new Component(profile, "test.a");

        var file = MakeVersionFile("test.a", "1.0", "Test A");
        file.Volatile = true;
        file.Requires.Add(new Require { Uid = "net.minecraft", EqualsVersion = "1.12.2" });
        component.File = file;
        component.Loaded = true;

        component.UpdateCachedData();

        Assert.Equal("1.0", component.GetVersion());
        Assert.Equal("Test A", component.GetName());
        Assert.True(component.CachedVolatile);
        Assert.Contains(component.CachedRequires, r => r.Uid == "net.minecraft" && r.EqualsVersion == "1.12.2");

        // the component must not alias the version file's set
        file.Requires.Clear();
        Assert.Contains(component.CachedRequires, r => r.Uid == "net.minecraft" && r.EqualsVersion == "1.12.2");
    }

    [Fact]
    public void ProblemsReportedForUnloadedPatch()
    {
        using var profile = new PackProfile(_root);
        var component = new Component(profile, "test.a");

        // without a version file only the "not loaded" placeholder is reported (C++ semantics)
        Assert.Equal(ProblemSeverity.Error, component.GetProblemSeverity());
        var problems = component.GetProblems();
        Assert.Single(problems);
        Assert.Contains("not loaded", problems[0].Description);

        // with a version file, added component problems are visible again
        component.File = MakeVersionFile("test.a", "1.0", "Test A");
        component.AddComponentProblem(ProblemSeverity.Warning, "a warning");
        component.AddComponentProblem(ProblemSeverity.Error, "an error");
        Assert.Equal(ProblemSeverity.Error, component.GetProblemSeverity());
        Assert.Equal(2, component.GetProblems().Count);

        component.ResetComponentProblems();
        Assert.Empty(component.GetProblems());
        Assert.Equal(ProblemSeverity.None, component.GetProblemSeverity());
    }

    [Fact]
    public void CustomizeWritesPatchFileAndRevertDeletesIt()
    {
        using var profile = new PackProfile(_root);
        var component = new Component(profile, "net.minecraft");

        // meta-backed component: customization materializes the version file into patches/
        var file = MakeVersionFile("net.minecraft", "1.12.2", "Minecraft");
        var metaVersion = new MetaVersion("net.minecraft", "1.12.2");
        metaVersion.SetData(file);
        component.MetaVersion = metaVersion;
        component.Loaded = true;

        Assert.False(component.IsCustom());
        Assert.True(component.IsCustomizable());
        Assert.False(component.IsRevertible()); // no metadata for the uid in the index

        Assert.True(component.Customize());
        Assert.True(component.IsCustom());
        Assert.Null(component.GetMeta());

        var patchPath = profile.PatchFilePathForUid("net.minecraft");
        Assert.True(File.Exists(patchPath));
        var patch = JsonNode.Parse(File.ReadAllText(patchPath))!;
        Assert.Equal("net.minecraft", patch["uid"]!.ToString());
        Assert.Equal("1.12.2", patch["version"]!.ToString());

        // reverting deletes the file again
        Assert.True(component.Revert());
        Assert.False(File.Exists(patchPath));
        Assert.False(component.IsCustom());
    }

    [Fact]
    public void RevertOnNonCustomComponentIsNoOp()
    {
        using var profile = new PackProfile(_root);
        var component = new Component(profile, "test.a");
        Assert.True(component.Revert());
    }

    [Fact]
    public void UpdateActionVariantRoundTrip()
    {
        using var profile = new PackProfile(_root);
        var component = new Component(profile, "test.a");

        Assert.IsType<UpdateAction.None>(component.GetUpdateAction());

        component.SetUpdateAction(new UpdateAction.ChangeVersion("1.2.3"));
        Assert.Equal("1.2.3", Assert.IsType<UpdateAction.ChangeVersion>(component.GetUpdateAction()).TargetVersion);

        component.SetUpdateAction(new UpdateAction.LatestRecommendedCompatible("net.minecraft", "Minecraft", "1.12.2"));
        var lrc = Assert.IsType<UpdateAction.LatestRecommendedCompatible>(component.GetUpdateAction());
        Assert.Equal("net.minecraft", lrc.ParentUid);
        Assert.Equal("1.12.2", lrc.Version);

        component.SetUpdateAction(new UpdateAction.Remove());
        Assert.IsType<UpdateAction.Remove>(component.GetUpdateAction());

        component.SetUpdateAction(new UpdateAction.ImportantChanged("1.0"));
        Assert.Equal("1.0", Assert.IsType<UpdateAction.ImportantChanged>(component.GetUpdateAction()).OldVersion);

        component.ClearUpdateAction();
        Assert.IsType<UpdateAction.None>(component.GetUpdateAction());
    }

    [Fact]
    public void ApplyToSkipsDisabledComponents()
    {
        using var profile = new PackProfile(_root);
        var component = new Component(profile, "test.a", MakeVersionFile("test.a", "1.0", "Test A"));
        component.SetEnabled(false);

        var launchProfile = new LaunchProfile();
        component.ApplyTo(launchProfile);
        Assert.Equal("", launchProfile.GetMainClass()); // nothing applied

        // and a component without a version file propagates its problem severity
        var broken = new Component(profile, "test.b");
        broken.AddComponentProblem(ProblemSeverity.Error, "broken");
        broken.ApplyTo(launchProfile);
        Assert.Equal(ProblemSeverity.Error, launchProfile.GetProblemSeverity());
    }
}
