// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/tasks/GetModDependenciesTask.{h,cpp}: the recursive
// dependency resolver. Given the selected (pack, version) pairs and the instance's
// mc-version/loader filter, walk every required entry of each version's `dependencies`,
// skip what is already installed/selected, resolve each remaining dependency through the
// provider API (project info + dependency version lookup) and expose the resulting
// PackDependency list (plus the extra "required by"/maybe-installed info for the UI).
//
// Port notes:
//  - The C++ Qt callback plumbing (ResourceAPI::getProject / getDependencyVersion with
//    Callback<IndexedVersion>) becomes small NetJob factories here; getDependencyVersion is
//    ported verbatim from ResourceAPI.cpp (response shape dispatch, loader heuristic,
//    newest-date sort). The per-provider loadIndexedPack/loadIndexedPackVersion virtuals
//    become the provider dispatch helpers below.
//  - The C++ `m_failed.remove(...)` calls on the two NetJob::failed handlers are no-ops in
//    this port (they erase the NetJob from the *parent* ConcurrentTask's failure map even
//    though the direct subtask is the wrapping SequentialTask, so upstream still fails);
//    the observable behavior (removePack + parent failure through the SequentialTask
//    chain) is preserved.
//  - instance->resourceLists() (shader/resource-pack/texture-pack metadata sweeps) is not
//    ported: only the mod folder model exists in this codebase so far.
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Dependencies;

public class GetModDependenciesTask : SequentialTask
{
    public class PackDependency
    {
        public Dependency? Dependency { get; set; }
        public IndexedPack Pack { get; set; } = new();
        public IndexedVersion Version { get; set; } = new();

        public PackDependency()
        {
        }

        public PackDependency(IndexedPack pack, IndexedVersion version)
        {
            Pack = pack;
            Version = version;
        }
    }

    public sealed record PackDependencyExtraInfo(bool MaybeInstalled, List<string> RequiredByNames, List<string> RequiredByIds);

    public GetModDependenciesTask(MinecraftInstance instance, ModFolderModel folder, List<PackDependency> selected,
                                  ModrinthApi? modrinth = null, FlameApi? flame = null)
    {
        _selected = selected;
        _flame = flame ?? new FlameApi();
        _version = new McVersion(instance.GetPackProfile().GetComponentVersion("net.minecraft"));
        _loaderType = instance.GetPackProfile().GetSupportedModLoaders() ?? ModLoaderType.None;
        _modrinth = modrinth ?? new ModrinthApi();

        foreach (var mod in folder.AllMods())
        {
            _modsFileNames.Add(mod.Fileinfo.Name);
            var meta = folder.GetMetadata(mod);
            if (meta is not null)
            {
                _mods.Add(meta);
            }
        }

        // (the C++ also sweeps instance->resourceLists() for metadata; those folder models
        //  are not ported yet — see the header note)

        Prepare();
    }

    /// <summary>Port of getDependecies() (sic): the resolved dependencies.</summary>
    public List<PackDependency> GetDependencies() => _packDependencies;

    private readonly List<PackDependency> _packDependencies = [];
    private readonly List<MetadataHandler.ModStruct> _mods = [];
    private readonly List<PackDependency> _selected;
    private readonly List<string> _modsFileNames = [];

    private readonly McVersion _version;
    private readonly ModLoaderType _loaderType;
    private readonly ModrinthApi _modrinth;
    private readonly FlameApi _flame;

    private ResourceApi GetApi(ResourceProvider provider)
    {
        if (provider == ResourceProvider.FLAME)
        {
            return _flame;
        }
        return _modrinth;
    }

    private void Prepare()
    {
        foreach (var sel in _selected)
        {
            if (CheckDependencies(sel, _version, _loaderType))
            {
                foreach (var dep in GetDependenciesForVersion(sel.Version, sel.Pack.Provider))
                {
                    AddTask(PrepareDependencyTask(dep, sel.Pack.Provider, 20));
                }
            }
        }
    }

    private Dependency GetOverride(Dependency dep, ResourceProvider providerName)
    {
        var isQuilt = (_loaderType & ModLoaderType.Quilt) != 0;
        if (isQuilt || (_loaderType & ModLoaderType.Fabric) != 0)
        {
            var overrides = ModIndex.GetOverrideDeps();
            foreach (var o in overrides)
            {
                // when on quilt, a fabric dep id is overridden with the quilt one, and vice versa
                if (o.Provider == providerName && dep.AddonId == (isQuilt ? o.Fabric : o.Quilt))
                {
                    return new Dependency { AddonId = isQuilt ? o.Quilt : o.Fabric, Type = dep.Type, Version = "" };
                }
            }
        }
        return dep;
    }

    private List<Dependency> GetDependenciesForVersion(IndexedVersion version, ResourceProvider providerName)
    {
        var cDependencies = new List<Dependency>();
        foreach (var verDepSource in version.Dependencies)
        {
            if (verDepSource.Type.Value != DependencyTypeValue.REQUIRED)
            {
                continue;
            }
            // the C++ iterates by value; getOverride may replace the entry wholesale
            var verDep = new Dependency { AddonId = verDepSource.AddonId, Type = verDepSource.Type, Version = verDepSource.Version };
            verDep = GetOverride(verDep, providerName);

            // A Modrinth dependency can pin only a version, with an empty project id
            var isOnlyVersion = providerName == ResourceProvider.MODRINTH && verDep.AddonId.Length == 0;

            if (cDependencies.Exists(i => IsSameDependency(i, verDep, isOnlyVersion)))
            {
                continue; // check the current dependency list
            }
            if (_selected.Exists(i => i.Pack.Provider == providerName &&
                                      (isOnlyVersion ? i.Version.Version == verDep.Version : i.Pack.AddonId == verDep.AddonId)))
            {
                continue; // check the selected versions
            }
            if (_mods.Exists(i => i.Provider == providerName &&
                                  (isOnlyVersion ? i.FileId == verDep.Version : i.ProjectId == verDep.AddonId)))
            {
                continue; // check the existing mods
            }
            if (_packDependencies.Exists(i => i.Pack.Provider == providerName &&
                                              (isOnlyVersion ? i.Version.Version == verDep.Version : i.Pack.AddonId == verDep.AddonId)))
            {
                continue; // check loaded dependencies
            }

            cDependencies.Add(verDep);
        }
        return cDependencies;

        static bool IsSameDependency(Dependency i, Dependency verDep, bool isOnlyVersion) =>
            isOnlyVersion ? i.Version == verDep.Version : i.AddonId == verDep.AddonId;
    }

    /// <summary>Port of ResourceAPI::getProject for both providers.</summary>
    private (NetJob? Job, NetRequest? Request) GetProject(ResourceProvider provider, string addonId)
    {
        if (provider == ResourceProvider.FLAME)
        {
            var (job, request) = _flame.GetProject(addonId);
            return (job, request);
        }
        return _modrinth.GetProject(addonId);
    }

    /// <summary>Port of the per-provider loadIndexedPack virtual.</summary>
    private static void LoadIndexedPack(ResourceProvider provider, IndexedPack pack, JsonNode obj)
    {
        if (provider == ResourceProvider.FLAME)
        {
            FlameMod.LoadIndexedPack(pack, obj);
        }
        else
        {
            ModrinthPackIndex.LoadIndexedPack(pack, obj);
        }
    }

    /// <summary>Port of the per-provider loadIndexedPackVersion virtual (Mod resource type).</summary>
    private static IndexedVersion LoadIndexedPackVersion(ResourceProvider provider, JsonNode obj)
    {
        return provider == ResourceProvider.FLAME
            ? FlameMod.LoadIndexedPackVersion(obj)
            : ModrinthPackIndex.LoadIndexedPackVersion(obj);
    }

    private FlintTask GetProjectInfoTask(PackDependency pDep)
    {
        var provider = pDep.Pack.Provider;
        var (job, request) = GetProject(provider, pDep.Pack.AddonId);
        if (job is null || request is null)
        {
            // the C++ cannot build the request either (getProject returned {nullptr, nullptr})
            RemovePack(pDep.Pack.AddonId);
            var failed = new SequentialTask("DependencyInfo");
            return failed;
        }

        job.Succeeded += _ =>
        {
            JsonNode root;
            try
            {
                root = JsonNode.Parse(request.BodyAsString)
                    ?? throw new JsonException("empty response");
            }
            catch (JsonException)
            {
                // (upstream logs the parse error) — qWarning() branch
                RemovePack(pDep.Pack.AddonId);
                return;
            }

            {
                try
                {
                    // Flame nests the project under "data" (the C++
                    // Json::requireObject(Json::requireObject(doc), "data") form; the string
                    // argument is a KEY there, which FlameJson.RequireObject mirrors)
                    JsonNode obj = provider == ResourceProvider.FLAME
                        ? FlameJson.RequireObject(root, "data")
                        : root;

                    LoadIndexedPack(provider, pDep.Pack, obj);
                }
                catch (JsonParseException)
                {
                    // (upstream logs the error cause)
                    RemovePack(pDep.Pack.AddonId);
                }
            }
        };
        job.Failed += (_, _) => RemovePack(pDep.Pack.AddonId);
        return job;
    }

    private FlintTask PrepareDependencyTask(Dependency dep, ResourceProvider providerName, int level)
    {
        var pDep = new PackDependency
        {
            Dependency = dep,
            Pack = new IndexedPack { AddonId = dep.AddonId, Provider = providerName },
        };

        _packDependencies.Add(pDep);

        var tasks = new SequentialTask(
            $"DependencyInfo: {(dep.AddonId.Length == 0 ? dep.Version : dep.AddonId)}");

        if (dep.AddonId.Length != 0)
        {
            tasks.AddTask(GetProjectInfoTask(pDep));
        }

        tasks.AddTask(DependencyVersionTask(dep, providerName, pDep, level));
        return tasks;
    }

    /// <summary>
    /// Port of ResourceAPI::getDependencyVersion (modplatform/ResourceAPI.cpp) wired to the
    /// onSucceed continuation of GetModDependenciesTask::prepareDependencyTask.
    /// </summary>
    private FlintTask DependencyVersionTask(Dependency dep, ResourceProvider provider, PackDependency pDep, int level)
    {
        var args = new DependencySearchArgs
        {
            Dependency = dep,
            McVersion = _version,
            Loader = _loaderType,
            IncludeChangelog = true,
        };

        var job = new NetJob($"{dep.AddonId}::Dependency");
        var request = NetRequest.MakeByteArray(new Uri(GetApi(provider).GetDependencyURL(args)!));
        job.AddNetRequest(request);

        job.Succeeded += _ =>
        {
            IndexedVersion bestMatch;
            JsonNode root;
            try
            {
                root = JsonNode.Parse(request.BodyAsString) ?? throw new JsonException("empty response");
            }
            catch (JsonException)
            {
                // like the C++: warn (upstream logs) and leave the version unset (the
                // netJob still succeeds)
                return;
            }

            {
                List<JsonNode> arr;
                if (dep.Version.Length != 0 && root is JsonObject)
                {
                    arr = [root];
                }
                else if (root is JsonObject)
                {
                    // the Flame {"data": [...]} shape; anything else parses to an empty list
                    var data = root["data"];
                    arr = data is JsonArray dataArray ? JsonUtils.ToNodeList(dataArray) : [];
                }
                else if (root is JsonArray rootArray)
                {
                    arr = JsonUtils.ToNodeList(rootArray);
                }
                else
                {
                    arr = [];
                }

                var versions = new List<IndexedVersion>();
                foreach (var entry in arr)
                {
                    if (entry is not JsonObject)
                    {
                        continue;
                    }

                    IndexedVersion file;
                    try
                    {
                        file = LoadIndexedPackVersion(provider, entry);
                    }
                    catch (JsonParseException)
                    {
                        continue;
                    }

                    if (file.AddonId.Length == 0)
                    {
                        file.AddonId = dep.AddonId;
                    }

                    // Heuristic to check if the returned value is valid
                    if (file.FileId.Length != 0 && (file.Loaders == ModLoaderType.None || (args.Loader & file.Loaders) != 0))
                    {
                        versions.Add(file);
                    }
                }

                // dates are in RFC 3339 format
                versions.Sort((a, b) => string.CompareOrdinal(b.Date, a.Date));
                bestMatch = versions.Count != 0 ? versions[0] : new IndexedVersion();
            }

            OnDependencyVersionResolved(dep, provider, pDep, level, bestMatch);
        };

        job.Failed += (_, _) => RemovePack(pDep.Pack.AddonId);
        return job;
    }

    /// <summary>The onSucceed continuation of the C++ prepareDependencyTask.</summary>
    private void OnDependencyVersionResolved(Dependency dep, ResourceProvider provider, PackDependency pDep, int level,
        IndexedVersion pack)
    {
        pDep.Version = pack;
        if (pDep.Version.AddonId.Length == 0)
        {
            if ((_loaderType & ModLoaderType.Quilt) != 0)
            {
                // fallback for quilt: the resolved id is a fabric id, retry with the quilt one
                foreach (var o in ModIndex.GetOverrideDeps())
                {
                    if (o.Provider == provider && dep.AddonId == o.Quilt)
                    {
                        RemovePack(dep.AddonId);
                        AddTask(PrepareDependencyTask(new Dependency { AddonId = o.Fabric, Type = dep.Type, Version = "" },
                            provider, level));
                        return;
                    }
                }
            }
            RemovePack(dep.AddonId);
            return;
        }
        pDep.Version.IsCurrentlySelected = true;
        pDep.Pack.Versions.Clear();
        pDep.Pack.Versions.Add(pDep.Version);
        pDep.Pack.VersionsLoaded = true;

        if (level == 0)
        {
            RemovePack(dep.AddonId);
            // qWarning("Dependency cycle exceeded") upstream
            return;
        }
        if (dep.AddonId.Length == 0 && pDep.Version.AddonId.Length != 0)
        {
            pDep.Pack.AddonId = pDep.Version.AddonId;
            var overrideDep = GetOverride(new Dependency { AddonId = pDep.Version.AddonId, Type = pDep.Dependency!.Type, Version = "" },
                provider);
            if (overrideDep.AddonId != pDep.Version.AddonId)
            {
                RemovePack(pDep.Version.AddonId);
                AddTask(PrepareDependencyTask(overrideDep, provider, level));
            }
            else
            {
                AddTask(GetProjectInfoTask(pDep));
            }
        }
        if (IsLocallyInstalled(pDep))
        {
            RemovePack(pDep.Version.AddonId);
            return;
        }
        foreach (var dependency in GetDependenciesForVersion(pDep.Version, provider))
        {
            AddTask(PrepareDependencyTask(dependency, provider, level - 1));
        }
    }

    private void RemovePack(string addonId)
    {
        _packDependencies.RemoveAll(v => v.Pack.AddonId == addonId);
    }

    public Dictionary<string, PackDependencyExtraInfo> GetExtraInfo()
    {
        var rby = new Dictionary<string, PackDependencyExtraInfo>(StringComparer.Ordinal);
        var fullList = _selected.Concat(_packDependencies).ToList();
        foreach (var mod in fullList)
        {
            var addonId = mod.Pack.AddonId;
            var provider = mod.Pack.Provider;
            var version = mod.Version.FileId;
            var reqNames = new List<string>();
            var reqIds = new List<string>();
            foreach (var smod in fullList)
            {
                if (provider != smod.Pack.Provider)
                {
                    continue;
                }
                var deps = smod.Version.Dependencies;
                var isRequiredByOther = deps.Any(d => d.Type.Value == DependencyTypeValue.REQUIRED &&
                    (provider == ResourceProvider.MODRINTH && d.AddonId.Length == 0 ? version == d.Version : d.AddonId == addonId));
                if (isRequiredByOther)
                {
                    reqNames.Add(smod.Pack.Name);
                    reqIds.Add(smod.Version.FileId);
                }
            }
            rby[addonId] = new PackDependencyExtraInfo(MaybeInstalled(mod), reqNames, reqIds);
        }
        return rby;
    }

    private bool IsLocallyInstalled(PackDependency pDep)
    {
        return pDep.Version.FileName.Length == 0 ||
               _selected.Exists(i => i.Version.FileName.Length != 0 && LaxCompare(i.Version.FileName, pDep.Version.FileName)) ||
               _modsFileNames.Exists(i => i.Length != 0 && LaxCompare(i, pDep.Version.FileName)) ||
               _packDependencies.Exists(i => pDep.Pack.AddonId != i.Pack.AddonId && i.Version.FileName.Length != 0 &&
                                             LaxCompare(pDep.Version.FileName, i.Version.FileName));
    }

    private bool MaybeInstalled(PackDependency pDep)
    {
        return _modsFileNames.Exists(i => i.Length != 0 && LaxCompare(i, pDep.Version.FileName, excludeDigits: true));
    }

    /// <summary>
    /// Port of the laxCompare helper: super lax (but not fuzzy) compare — lowercase,
    /// separators to whitespace, whitespace simplified; effectively compares two strings
    /// ignoring all separators and case.
    /// </summary>
    private static bool LaxCompare(string fsFilename, string metadataFilename, bool excludeDigits = false)
    {
        // allowed character separators
        var allowedSeparators = new List<char> { '-', '+', '.', '_' };
        if (excludeDigits)
        {
            allowedSeparators.AddRange("0123456789");
        }

        // copy in lowercase
        var fsName = fsFilename.ToLowerInvariant();
        var metaName = metadataFilename.ToLowerInvariant();

        // replace all potential allowed separators with whitespace
        foreach (var sep in allowedSeparators)
        {
            fsName = fsName.Replace(sep, ' ');
            metaName = metaName.Replace(sep, ' ');
        }

        return Simplified(fsName) == Simplified(metaName);
    }

    /// <summary>QString::simplified: trimmed, internal whitespace runs collapsed to one space.</summary>
    private static string Simplified(string s)
    {
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool CheckDependencies(PackDependency sel, McVersion mcVersion, ModLoaderType loaders)
    {
        return (sel.Pack.Versions.Count == 0 || sel.Version.McVersion.Contains(mcVersion.ToString())) &&
               (loaders == ModLoaderType.None || sel.Version.Loaders == ModLoaderType.None ||
                (sel.Version.Loaders & loaders) != 0);
    }
}
