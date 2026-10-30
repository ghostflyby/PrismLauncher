// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/mod/tasks/GetModDependenciesTask.{h,cpp}: the recursive
// dependency resolver. Given the selected (pack, version) pairs and the instance's
// mc-version/loader filter, walk every required entry of each version's `dependencies`,
// skip what is already installed/selected, resolve each remaining dependency through the
// provider API (project info + dependency version lookup) and expose the resulting
// PackDependency list (plus the extra "required by"/maybe-installed info for the UI).
//
// Port notes:
//  - The C++ Qt callback plumbing (ResourceAPI::getProject / getDependencyVersion with
//    Callback<IndexedVersion>) becomes direct awaits here; getDependencyVersion is ported
//    verbatim from ResourceAPI.cpp (response shape dispatch, loader heuristic, newest-date
//    sort). The per-provider loadIndexedPack/loadIndexedPackVersion virtuals become the
//    provider dispatch helpers below.
//  - The C++ `m_failed.remove(...)` calls on the two NetJob::failed handlers have no
//    counterpart: a failure removes the pack and propagates as the exception.
//  - instance->resourceLists() (shader/resource-pack/texture-pack metadata sweeps) is not
//    ported: only the mod folder model exists in this codebase so far.

using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;

namespace FlintLauncher.Core.ModPlatform.Dependencies;

public class GetModDependenciesTask
{
    public class PackDependency
    {
        public Dependency? Dependency { get; init; }
        public IndexedPack Pack { get; init; } = new();
        public IndexedVersion Version { get; set; } = new();

        public PackDependency() { }

        public PackDependency(IndexedPack pack, IndexedVersion version)
        {
            Pack = pack;
            Version = version;
        }
    }

    public sealed record PackDependencyExtraInfo(List<string> RequiredByIds);

    public GetModDependenciesTask(MinecraftInstance instance, ModFolderModel folder, List<PackDependency> selected,
        ModrinthClient modrinth, HttpClient http, FlameClient? flame = null)
    {
        _selected = selected;
        _flame = flame ?? new FlameClient();
        _version = new McVersion(instance.GetPackProfile().GetComponentVersion("net.minecraft"));
        _loaderType = instance.GetPackProfile().GetSupportedModLoaders() ?? ModLoaderType.None;
        _modrinth = modrinth;
        _http = http;

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
    }

    /// <summary>Resolves every selected pack's dependencies, depth-first. Returning is success;
    /// a failure aborts the walk.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var sel in _selected)
        {
            if (!CheckDependencies(sel, _version, _loaderType))
            {
                continue;
            }

            foreach (var dep in GetDependenciesForVersion(sel.Version, sel.Pack.Provider))
            {
                await PrepareDependencyAsync(dep, sel.Pack.Provider, 20, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Port of getDependecies() (sic): the resolved dependencies.</summary>
    public List<PackDependency> GetDependencies() => _packDependencies;

    private readonly List<PackDependency> _packDependencies = [];
    private readonly List<MetadataHandler.ModStruct> _mods = [];
    private readonly List<PackDependency> _selected;
    private readonly List<string> _modsFileNames = [];

    private readonly McVersion _version;
    private readonly ModLoaderType _loaderType;
    private readonly ModrinthClient _modrinth;
    private readonly HttpClient _http;
    private readonly FlameClient _flame;

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
            if (verDepSource.Type.Value != DependencyTypeValue.Required)
            {
                continue;
            }

            // the C++ iterates by value; getOverride may replace the entry wholesale
            var verDep = new Dependency { AddonId = verDepSource.AddonId, Type = verDepSource.Type, Version = verDepSource.Version };
            verDep = GetOverride(verDep, providerName);

            // A Modrinth dependency can pin only a version, with an empty project id
            var isOnlyVersion = providerName == ResourceProvider.Modrinth && verDep.AddonId.Length == 0;

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

    /// <summary>The project-info step for one dependency pack: fetch, parse into the pack,
    /// and drop the pack (failing the step) when the fetch or the shape fails.</summary>
    private async Task GetProjectInfoAsync(PackDependency pDep, CancellationToken cancellationToken)
    {
        var provider = pDep.Pack.Provider;
        try
        {
            if (provider == ResourceProvider.Flame)
            {
                try
                {
                    var envelope = await _flame.Projects.ById(pDep.Pack.AddonId)
                        .SendTo(_http, cancellationToken);
                    if (envelope.Data is { } project)
                    {
                        FlameMod.LoadIndexedPack(pDep.Pack, project);
                    }
                    else
                    {
                        throw new JsonException("'data' is not an object");
                    }
                }
                catch (FlameApiException e)
                {
                    throw new HttpRequestException(e.Message, e);
                }
            }
            else
            {
                var dto = await _modrinth.Projects.ById(pDep.Pack.AddonId).SendTo(_http, cancellationToken);
                ModrinthPackIndex.LoadIndexedPack(pDep.Pack, dto);
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or ModrinthApiException
                                      or FlameApiException)
        {
            // (upstream logs the parse error / the error cause)
            RemovePack(pDep.Pack.AddonId);
            throw;
        }
    }

    private async Task PrepareDependencyAsync(Dependency dep, ResourceProvider providerName, int level,
        CancellationToken cancellationToken)
    {
        var pDep = new PackDependency
        {
            Dependency = dep,
            Pack = new IndexedPack { AddonId = dep.AddonId, Provider = providerName },
        };

        _packDependencies.Add(pDep);

        if (dep.AddonId.Length != 0)
        {
            await GetProjectInfoAsync(pDep, cancellationToken).ConfigureAwait(false);
        }

        await ResolveDependencyVersionAsync(dep, providerName, pDep, level, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of ResourceAPI::getDependencyVersion (modplatform/ResourceAPI.cpp) wired to the
    /// onSucceed continuation of GetModDependenciesTask::prepareDependencyTask.
    /// </summary>
    private async Task ResolveDependencyVersionAsync(Dependency dep, ResourceProvider provider,
        PackDependency pDep, int level, CancellationToken cancellationToken)
    {
        // the per-provider candidate lists: the Flame branch answers the pinned
        // single-file document or the {"data": [...]} envelope, the Modrinth branch a
        // single version document or a bare array
        var versions = new List<IndexedVersion>();

        void Consider(IndexedVersion file)
        {
            if (file.AddonId.Length == 0)
            {
                file.AddonId = dep.AddonId;
            }

            // Heuristic to check if the returned value is valid
            if (file.FileId.Length != 0 && (_loaderType == ModLoaderType.None || (_loaderType & file.Loaders) != 0))
            {
                versions.Add(file);
            }
        }

        if (provider == ResourceProvider.Flame)
        {
            List<FlameModFile>? files;
            try
            {
                // the pinned dependency names a file; the rest list the project's files
                // for the instance's game version and single selected loader
                files = dep.Version.Length != 0
                    ?
                    [
                        (
                            await _flame.Files.ById(dep.AddonId, dep.Version).SendTo(_http, cancellationToken)).Data
                        ?? throw new JsonException("'data' is not an object")
                    ]
                    : (await _flame.Files.List(dep.AddonId,
                              gameVersion: _version.ToString(),
                              modLoaderType: _loaderType != 0 && ModIndex.HasSingleModLoaderSelected(_loaderType)
                                  ? FlameMod.GetMappedModLoader(_loaderType)
                                  : null)
                          .SendTo(_http, cancellationToken)).Data
                      ?? throw new JsonException("'data' is not an array");
            }
            catch (FlameApiException e) when (e.InnerException is HttpRequestException)
            {
                RemovePack(pDep.Pack.AddonId);
                throw new HttpRequestException(e.Message, e);
            }
            catch (Exception e) when (e is FlameApiException or JsonException)
            {
                // like the C++ kind-failure path: a broken body degrades to an empty
                // best match, which the resolution step turns into an explicit
                // missing-dependency (RemovePack) instead of leaving a version-less
                // pack dangling
                await OnDependencyVersionResolvedAsync(dep, provider, pDep, level, new IndexedVersion(),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var fileDto in files)
            {
                try
                {
                    Consider(FlameMod.LoadIndexedPackVersion(fileDto));
                }
                catch (JsonException) { }
            }
        }
        else
        {
            List<ModrinthVersion> dtos;
            try
            {
                dtos = dep.Version.Length != 0
                    ? [await _modrinth.Versions.ById(dep.Version).SendTo(_http, cancellationToken)]
                    : await _modrinth.Versions.Query(dep.AddonId,
                        [ModrinthVersionFormat.ToWire(_version)],
                        ModIndex.ModLoaderTypesToList(_loaderType).Select(ModIndex.GetModLoaderAsString).ToList(),
                        includeChangelog: true).SendTo(_http, cancellationToken);
            }
            catch (Exception e) when (e is ModrinthApiException)
            {
                RemovePack(pDep.Pack.AddonId);
                throw;
            }

            foreach (var dto in dtos)
            {
                try
                {
                    Consider(ModrinthPackIndex.LoadIndexedPackVersion(dto));
                }
                catch (JsonException)
                {
                    // this version entry is unusable; the rest still count
                }
            }
        }

        // dates are in RFC 3339 format
        versions.Sort((a, b) => string.CompareOrdinal(b.Date, a.Date));
        var bestMatch = versions.Count != 0 ? versions[0] : new IndexedVersion();
        await OnDependencyVersionResolvedAsync(dep, provider, pDep, level, bestMatch, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The onSucceed continuation of the C++ prepareDependencyTask.</summary>
    private async Task OnDependencyVersionResolvedAsync(Dependency dep, ResourceProvider provider,
        PackDependency pDep, int level, IndexedVersion pack, CancellationToken cancellationToken)
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
                        await PrepareDependencyAsync(
                            new Dependency { AddonId = o.Fabric, Type = dep.Type, Version = "" },
                            provider, level, cancellationToken).ConfigureAwait(false);
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

        if (dep.AddonId.Length == 0 && pDep.Version.AddonId.Length != 0 && pDep.Dependency is { } discovered)
        {
            pDep.Pack.AddonId = pDep.Version.AddonId;
            var overrideDep = GetOverride(new Dependency { AddonId = pDep.Version.AddonId, Type = discovered.Type, Version = "" },
                provider);
            if (overrideDep.AddonId != pDep.Version.AddonId)
            {
                RemovePack(pDep.Version.AddonId);
                await PrepareDependencyAsync(overrideDep, provider, level, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await GetProjectInfoAsync(pDep, cancellationToken).ConfigureAwait(false);
            }
        }

        if (IsLocallyInstalled(pDep))
        {
            RemovePack(pDep.Version.AddonId);
            return;
        }

        foreach (var dependency in GetDependenciesForVersion(pDep.Version, provider))
        {
            await PrepareDependencyAsync(dependency, provider, level - 1, cancellationToken)
                .ConfigureAwait(false);
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
            var reqIds = new List<string>();
            foreach (var smod in fullList)
            {
                if (provider != smod.Pack.Provider)
                {
                    continue;
                }

                var deps = smod.Version.Dependencies;
                var isRequiredByOther = deps.Any(d => d.Type.Value == DependencyTypeValue.Required &&
                                                      (provider == ResourceProvider.Modrinth && d.AddonId.Length == 0 ? version == d.Version : d.AddonId == addonId));
                if (isRequiredByOther)
                {
                    reqIds.Add(smod.Version.FileId);
                }
            }

            rby[addonId] = new PackDependencyExtraInfo(reqIds);
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
