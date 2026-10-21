// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/Component.{h,cpp}: the wrapper around one entry of the instance
// component list (either backed by remote metadata or a local patch file).
//
// Deviations from the C++ original:
//  - The UpdateAction std::variant becomes a closed set of records nested under
//    <see cref="UpdateAction"/> (UpdateActionNone is UpdateAction.None).
//  - The QObject parent pointer becomes the nullable <see cref="Parent"/> reference; the
//    APPLICATION->metadataIndex() singleton is reached through the parent PackProfile so the
//    component stays testable without global state.
//  - The dataChanged() signal becomes the DataChanged event; the C++ data members (m_uid,
//    m_version, ...) are public settable properties because PackProfile and the update task
//    mutate them directly (friend access upstream).
//  - The blocking waitToLoad()/waitLoadMeta() calls get async counterparts; the synchronous
//    wait is only available through WaitLoadMetaAsync.

using System.Text;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Net;
using TruePath;

namespace FlintLauncher.Core.Minecraft;

/// <summary>
/// Port of the UpdateAction std::variant (Component.h): the pending change the resolution
/// task applies to a component (std::monostate maps to <see cref="UpdateAction.None"/>).
/// </summary>
public abstract record UpdateAction
{
    /// <summary>std::monostate — no action.</summary>
    public sealed record None : UpdateAction;

    /// <summary>UpdateActionChangeVersion — change to an explicit target version.</summary>
    public sealed record ChangeVersion(string TargetVersion) : UpdateAction;

    /// <summary>UpdateActionLatestRecommendedCompatible — pick the recommended (or latest) version compatible with the parent.</summary>
    public sealed record LatestRecommendedCompatible(string ParentUid, string ParentName, string Version) : UpdateAction;

    /// <summary>UpdateActionRemove — remove the component.</summary>
    public sealed record Remove : UpdateAction;

    /// <summary>UpdateActionImportantChanged — the important component changed from OldVersion; linked components must follow.</summary>
    public sealed record ImportantChanged(string OldVersion) : UpdateAction;
}

public class Component : ProblemProvider
{
    /// <summary>Port of Component::KNOWN_MODLOADERS (QMap; iterated in uid order upstream).</summary>
    public static readonly IReadOnlyDictionary<string, ModloaderMapEntry> KnownModloaders =
        new Dictionary<string, ModloaderMapEntry>(StringComparer.Ordinal)
        {
            ["net.neoforged"] = new(ModLoaderType.NeoForge, ["net.minecraftforge", "net.fabricmc.fabric-loader", "org.quiltmc.quilt-loader"]),
            ["net.minecraftforge"] = new(ModLoaderType.Forge, ["net.neoforged", "net.fabricmc.fabric-loader", "org.quiltmc.quilt-loader"]),
            ["net.fabricmc.fabric-loader"] = new(ModLoaderType.Fabric, ["net.minecraftforge", "net.neoforged", "org.quiltmc.quilt-loader"]),
            ["org.quiltmc.quilt-loader"] = new(ModLoaderType.Quilt, ["net.minecraftforge", "net.neoforged", "net.fabricmc.fabric-loader"]),
            ["com.mumfrey.liteloader"] = new(ModLoaderType.LiteLoader, []),
        };

    private readonly List<PatchProblem> _componentProblems = [];
    private ProblemSeverity _componentProblemSeverity = ProblemSeverity.None;

    private UpdateAction UpdateActionValue { get; set; } = new UpdateAction.None();

    /// <summary>Port of the dataChanged() signal (UI plumbing).</summary>
    public event Action<Component>? DataChanged;

    public Component(PackProfile? parent, string uid)
    {
        Parent = parent;
        Uid = uid;
    }

    public Component(PackProfile? parent, string uid, VersionFile file)
    {
        Parent = parent;
        File = file;
        Uid = uid;
        CachedVersion = file.Version;
        CachedName = file.Name;
        Loaded = true;
    }

    // BEGIN: persistent component list properties (C++ public data section)

    /// ID of the component
    public string Uid { get; }

    /// version of the component - when there's a custom json override, this is also the version the component reverts to
    public string Version { get; set; } = "";

    /// if true, this has been added automatically to satisfy dependencies and may be automatically removed
    public bool DependencyOnly { get; set; }

    /// if true, the component is either the main component of the instance, or otherwise important and cannot be removed.
    public bool Important { get; set; }

    /// if true, the component is disabled
    public bool Disabled { get; set; }

    /// cached name for display purposes, taken from the version file (meta or local override)
    public string CachedName { get; set; } = "";

    /// cached version for display AND other purposes, taken from the version file (meta or local override)
    public string CachedVersion { get; set; } = "";

    /// cached set of requirements, taken from the version file (meta or local override)
    public SortedSet<Require> CachedRequires { get; set; } = new();

    /// cached set of conflicts, taken from the version file (meta or local override)
    public SortedSet<Require> CachedConflicts { get; set; } = new();

    /// if true, the component is volatile and may be automatically removed when no longer needed
    public bool CachedVolatile { get; set; }

    // END: persistent component list properties

    // DEPRECATED: explicit numeric order values, used for loading old non-component config
    public bool OrderOverride { get; set; }

    /// <summary>DEPRECATED: see <see cref="OrderOverride"/>.</summary>
    public int Order { get; set; }

    // load state
    public MetaVersion? MetaVersion { get; set; }

    public VersionFile? File { get; set; }

    public bool Loaded { get; set; }

    /// <summary>The PackProfile this component belongs to (C++ QObject parent; null in tests).</summary>
    public PackProfile? Parent { get; }

    // ---------------------------------------------------------------- queries

    public MetaVersion? GetMeta() => MetaVersion;

    public void ApplyTo(LaunchProfile profile)
    {
        // do not apply disabled components
        if (!IsEnabled())
        {
            return;
        }

        var vfile = GetVersionFile();
        if (vfile is not null)
        {
            vfile.ApplyTo(profile, Parent is not null ? Parent.RuntimeContext() : default);
        }
        else
        {
            profile.ApplyProblemSeverity(GetProblemSeverity());
        }
    }

    public VersionFile? GetVersionFile() => MetaVersion is not null ? MetaVersion.Data : File;

    public VersionList? GetVersionList()
    {
        // FIXME upstream: what if the metadata index isn't loaded yet?
        if (Parent is not null && Parent.MetadataIndex.HasUid(Uid))
        {
            return Parent.MetadataIndex.Get(Uid);
        }

        return null;
    }

    public int GetOrder()
    {
        if (OrderOverride)
        {
            return Order;
        }

        var vfile = GetVersionFile();
        if (vfile is not null)
        {
            return vfile.Order;
        }

        return 0;
    }

    public void SetOrder(int order)
    {
        OrderOverride = true;
        Order = order;
    }

    public string GetId() => Uid;

    public string GetName()
    {
        if (CachedName.Length != 0)
        {
            return CachedName;
        }

        return Uid;
    }

    public string GetVersion() => CachedVersion;

    public string GetFilename() => Parent is not null ? Parent.PatchFilePathForUid(Uid) : "";

    public DateTimeOffset GetReleaseDateTime()
    {
        if (MetaVersion is not null)
        {
            return MetaVersion.Time();
        }

        var vfile = GetVersionFile();
        if (vfile is not null && vfile.ReleaseTime is not null)
        {
            return vfile.ReleaseTime.Value;
        }

        // FIXME upstream: fake
        return DateTimeOffset.Now;
    }

    // ---------------------------------------------------------------- flags

    public bool IsEnabled() => !CanBeDisabled() || !Disabled;

    public bool CanBeDisabled() => IsRemovable() && !DependencyOnly;

    public bool SetEnabled(bool state)
    {
        var intendedDisabled = !state;
        if (!CanBeDisabled())
        {
            intendedDisabled = false;
        }

        if (intendedDisabled != Disabled)
        {
            Disabled = intendedDisabled;
            EmitDataChanged();
            return true;
        }

        return false;
    }

    public bool IsCustom() => File is not null;

    public bool IsCustomizable() => MetaVersion is not null && GetVersionFile() is not null;

    public bool IsRemovable() => !Important;

    public bool IsRevertible()
    {
        if (IsCustom())
        {
            if (Parent is not null && Parent.MetadataIndex.HasUid(Uid))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsMoveable()
    {
        // HACK upstream FIXME: this was too dumb and wouldn't follow dependency constraints
        // anyway. For now hardcoded to 'true'.
        return true;
    }

    public bool IsVersionChangeable(bool wait = true)
    {
        var list = GetVersionList();
        if (list is not null)
        {
            // NOTE: the C++ wait=true path blocks in VersionList::waitToLoad(); the port has no
            // blocking wait, so callers must make sure the version list is loaded beforehand
            // (for the Resolution flow it always is).
            return list.Versions().Count != 0;
        }

        return false;
    }

    public bool IsKnownModloader() => KnownModloaders.ContainsKey(Uid);

    public IReadOnlyList<string> KnownConflictingComponents()
    {
        return KnownModloaders.TryGetValue(Uid, out var entry) ? entry.KnownConflictingComponents : [];
    }

    public void SetImportant(bool state)
    {
        if (Important != state)
        {
            Important = state;
            EmitDataChanged();
        }
    }

    // ---------------------------------------------------------------- problems

    public override ProblemSeverity GetProblemSeverity()
    {
        var file = GetVersionFile();
        if (file is not null)
        {
            var severity = file.GetProblemSeverity();
            return _componentProblemSeverity > severity ? _componentProblemSeverity : severity;
        }

        return ProblemSeverity.Error;
    }

    public override IReadOnlyList<PatchProblem> GetProblems()
    {
        var file = GetVersionFile();
        if (file is not null)
        {
            var problems = new List<PatchProblem>(file.GetProblems());
            problems.AddRange(_componentProblems);
            return problems;
        }

        return [new PatchProblem(ProblemSeverity.Error, "Patch is not loaded yet.")];
    }

    public void AddComponentProblem(ProblemSeverity severity, string description)
    {
        if (severity > _componentProblemSeverity)
        {
            _componentProblemSeverity = severity;
        }

        _componentProblems.Add(new PatchProblem(severity, description));
        EmitDataChanged();
    }

    public void ResetComponentProblems()
    {
        _componentProblems.Clear();
        _componentProblemSeverity = ProblemSeverity.None;
        EmitDataChanged();
    }

    // ---------------------------------------------------------------- mutation

    public void SetVersion(string version)
    {
        if (version == Version)
        {
            return;
        }

        Version = version;
        if (Loaded)
        {
            // we are loaded and potentially have state to invalidate
            if (File is not null)
            {
                // we have a file... explicit version has been changed and there is nothing else to do.
            }
            else if (Parent is not null)
            {
                // we don't have a file, therefore we are loaded with metadata
                CachedVersion = version;
                // see if the meta version is loaded
                var metaVersion = Parent.MetadataIndex.Get(Uid, version);
                if (metaVersion.IsLoaded)
                {
                    // if yes, we can continue with that.
                    MetaVersion = metaVersion;
                }
                else
                {
                    // if not, we need loading
                    MetaVersion = null;
                    Loaded = false;
                }

                UpdateCachedData();
            }
        }

        // not loaded... assume it will be sorted out later by the update task
        EmitDataChanged();
    }

    public bool Customize()
    {
        if (IsCustom())
        {
            return false;
        }

        var filename = GetFilename();
        if (!FileOps.EnsureFilePathExists(new LocalPath(filename)))
        {
            return false;
        }

        // FIXME upstream: get rid of this try-catch.
        try
        {
            var vfile = GetVersionFile();
            if (vfile is null)
            {
                return false;
            }

            FileOps.Write(new LocalPath(filename), Encoding.UTF8.GetBytes(OneSixVersionFormat.SerializeVersionFile(vfile)));
            File = vfile;
            MetaVersion = null;
            EmitDataChanged();
        }
        catch (Exception)
        {
            // NOTE upstream quirk kept: the exception is logged ("Version could not be loaded")
            // but Customize() still returns true (the catch does not set a failure result)
        }

        return true;
    }

    public bool Revert()
    {
        if (!IsCustom())
        {
            // already not custom
            return true;
        }

        var filename = GetFilename();
        var result = true;
        // just kill the file and reload
        if (System.IO.File.Exists(filename))
        {
            result = FileOps.DeletePath(new LocalPath(filename));
        }

        if (result)
        {
            // file gone...
            File = null;

            // check local cache for metadata...
            if (Parent is not null)
            {
                var version = Parent.MetadataIndex.Get(Uid, Version);
                if (version.IsLoaded)
                {
                    MetaVersion = version;
                }
                else
                {
                    MetaVersion = null;
                    Loaded = false;
                }
            }

            EmitDataChanged();
        }

        return result;
    }

    /// <summary>deep inspecting compare for requirement sets — compares all fields of the Require structs.</summary>
    /// <summary>deep inspecting compare for requirement sets — compares all fields of the Require structs.</summary>
    private static bool DeepCompare(SortedSet<Require> a, SortedSet<Require> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var reqA in a)
        {
            if (!b.Any(reqB => reqA.DeepEquals(reqB)))
            {
                return false;
            }
        }

        return true;
    }

    public void UpdateCachedData()
    {
        var file = GetVersionFile();
        if (file is not null)
        {
            var changed = false;
            if (CachedName != file.Name)
            {
                CachedName = file.Name;
                changed = true;
            }

            if (CachedVersion != file.Version)
            {
                CachedVersion = file.Version;
                changed = true;
            }

            if (CachedVolatile != file.Volatile)
            {
                CachedVolatile = file.Volatile;
                changed = true;
            }

            if (!DeepCompare(CachedRequires, file.Requires))
            {
                // the C++ std::set assignment copies; clone so the component does not alias the file
                CachedRequires = [.. file.Requires];
                changed = true;
            }

            if (!DeepCompare(CachedConflicts, file.Conflicts))
            {
                CachedConflicts = [.. file.Conflicts];
                changed = true;
            }

            if (changed)
            {
                EmitDataChanged();
            }
        }
        else
        {
            // in case we removed all the metadata
            CachedRequires.Clear();
            CachedConflicts.Clear();
            EmitDataChanged();
        }
    }

    /// <summary>
    /// Async port of Component::waitLoadMeta() (the C++ version blocks in
    /// Index::getLoadedVersion()'s event loop).
    /// </summary>
    public async Task WaitLoadMetaAsync(NetMode mode = NetMode.Online, CancellationToken cancellationToken = default)
    {
        if (!Loaded)
        {
            if (Parent is not null && (MetaVersion is null || !MetaVersion.IsLoaded))
            {
                // wait for the loaded version from meta
                await Parent.MetadataIndex.LoadVersion(Uid, Version, mode).StartAsync(cancellationToken);
                MetaVersion = Parent.MetadataIndex.Get(Uid, Version);
            }

            Loaded = true;
            UpdateCachedData();
        }
    }

    // ---------------------------------------------------------------- update actions

    public void SetUpdateAction(UpdateAction action) => UpdateActionValue = action;

    public UpdateAction GetUpdateAction() => UpdateActionValue;

    public void ClearUpdateAction() => UpdateActionValue = new UpdateAction.None();

    private void EmitDataChanged() => DataChanged?.Invoke(this);

    public override string ToString() => $"Component({Uid} : {CachedVersion})";
}
