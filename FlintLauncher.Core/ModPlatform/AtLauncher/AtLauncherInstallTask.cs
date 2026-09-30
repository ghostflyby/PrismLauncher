// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLPackInstallTask.{h,cpp}: install an ATLauncher pack
// version — fetch Configs.json, download+extract Configs.zip into minecraft/, download the
// mods (md5-verified; ATL CDN "server" or "direct" URLs), extract/copy them into the pack
// layout and create the instance (net.minecraft + loader component, optional libraries and
// pack components via patches/, jar mods).
//
// The C++ UserInteractionSupport dialogs become pre-set inputs (the Flame optional handling
// pattern):
//  - chooseOptionalMods -> OptionalModsSelection (mod name -> selected)
//  - chooseVersion      -> ChooseLoaderVersion (loader "choose" mode)
//  - displayMessage     -> DisplayMessage hook
//  - the BlockedModsDialog -> BlockedMods + BlockedModLocalFileResolver (like
//    FlameCreationTask; the C++ reject path fails "Unknown download type: browser")
//
// Deviations (headless port):
//  - getComponentVersion consults the static MetaVersionDataLookup hook (the meta index
//    layer is not wired in this phase). Unlike the C++ — which fails the install when
//    net.minecraft is not in the local index — the lookup is best-effort: its only use is
//    a null-checked library exemption, so a miss only skips that optimization. The
//    Forge/LiteLoader lookups keep the C++ null paths (fall back to a jar mod).
//  - getVersionForLoader's recommended/latest branch resolves through the static
//    RecommendedLoaderVersionLookup hook (the version-list traversal is not ported);
//    without a hook the install fails like the C++ does when the version list cannot
//    be loaded.
//  - Downloads use the meta cache when MetaCache is set ("ATLauncherPacks" base), otherwise
//    a plain download into a temporary folder (the C++ always goes through the cache).
//  - downloadFiles() (the P5 update pipeline) is not ported; creation succeeds without
//    fetching game files, like the other creation tasks.
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

public sealed partial class AtLauncherPackInstallTask : InstanceTask
{
    /// <summary>Port of ATLauncher::InstallMode.</summary>
    public enum InstallMode : byte
    {
        Install,
        Reinstall,
        Update,
    }

    public AtLauncherPackInstallTask(string packName, string version, InstallMode installMode = InstallMode.Install)
    {
        PackName = packName;
        PackSafeName = AtLauncherPackIndex.SafePackName(packName);
        VersionName = version;
        InstallModeValue = installMode;
    }

    public string PackName { get; }
    public string PackSafeName { get; }
    public string VersionName { get; }
    public InstallMode InstallModeValue { get; }

    /// <summary>APPLICATION->metacache() hook ("ATLauncherPacks" base); null = plain downloads.</summary>
    public HttpMetaCache? MetaCache { get; set; }

    // ------------------------------------------------------- user-interaction inputs

    /// <summary>chooseOptionalMods stand-in: the optional mods (by name) that are selected.</summary>
    public IReadOnlyDictionary<string, bool> OptionalModsSelection { get; set; } =
        new Dictionary<string, bool>();

    /// <summary>chooseVersion stand-in (loader "choose" mode): (uid, minecraft version or null
    /// for fabric) -> the chosen version descriptor.</summary>
    public Func<string, string?, string?>? ChooseLoaderVersion { get; set; }

    /// <summary>displayMessage stand-in (the C++ shows a modal before continuing).</summary>
    public Action<string>? DisplayMessage { get; set; }

    /// <summary>BlockedModsDialog stand-in: given a blocked mod, return a matching local file
    /// path (null when the user has none).</summary>
    public Func<Flame.BlockedMod, string?>? BlockedModLocalFileResolver { get; set; }

    /// <summary>The blocked (browser-download) files reported when the resolver ran.</summary>
    public IReadOnlyList<Flame.BlockedMod> BlockedMods { get; private set; } = [];

    // ------------------------------------------------------------- meta layer hooks

    /// <summary>(uid, version) -> loaded VersionFile from the local metadata index
    /// (APPLICATION->metadataIndex()->getLoadedVersion). Best-effort — see the header note.</summary>
    public static Func<string, string, VersionFile?>? MetaVersionDataLookup { get; set; }

    /// <summary>(loader uid, loader type, minecraft version) -> version descriptor for the
    /// recommended/latest loader resolution.</summary>
    public static Func<string, string, string, string?>? RecommendedLoaderVersionLookup { get; set; }

    // -------------------------------------------------------------------- state

    private readonly PackVersion _version = new();
    private string _archivePath = "";
    private readonly List<string> _jarMods = [];
    private VersionFile? _minecraftVersionData;
    private readonly Dictionary<string, string> _componentsToInstall = new();
    private readonly Dictionary<string, VersionMod> _modsToExtract = new();
    private readonly Dictionary<string, VersionMod> _modsToDecomp = new();
    private readonly Dictionary<string, string> _modsToCopy = new();

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Fetching pack version...");

        var netJob = new NetJob("ATLauncher::VersionFetch");
        var request = NetRequest.MakeByteArray(new Uri(AtLauncherApi.PackConfigsUrl(PackSafeName, VersionName)));
        netJob.AddNetRequest(request);
        await netJob.StartAsync(cancellationToken);
        if (!netJob.WasSuccessful)
        {
            EmitFailed(netJob.FailReason);
            return;
        }
        await OnDownloadSucceeded(request.BodyAsString, cancellationToken);
    }

    private async Task OnDownloadSucceeded(string response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonUtils.RequireDocument(response, "JSON response from ATLauncher");
            AtLauncherPackManifest.LoadVersion(_version, AtLauncherJson.Object(doc.RootElement));
        }
        catch (JsonParseException e)
        {
            EmitFailed("Could not understand pack manifest:\n" + e.Message);
            return;
        }

        // Derived from the installation mode
        string message;
        bool resetDirectory;

        switch (InstallModeValue)
        {
            case InstallMode.Reinstall:
            case InstallMode.Update:
                message = _version.Messages.Update;
                resetDirectory = true;
                break;

            case InstallMode.Install:
                message = _version.Messages.Install;
                resetDirectory = false;
                break;

            default:
                EmitFailed("Unsupported installation mode");
                return;
        }

        // Display message if one exists
        if (message.Length != 0)
        {
            DisplayMessage?.Invoke(message);
        }

        // (the C++ fails the install when net.minecraft is not in the local metadata index;
        //  see the header note for the best-effort deviation)
        _minecraftVersionData = MetaVersionDataLookup?.Invoke("net.minecraft", _version.Minecraft);

        if (resetDirectory)
        {
            DeleteExistingFiles();
        }

        if (_version.NoConfigs)
        {
            await DownloadMods(cancellationToken);
        }
        else
        {
            await InstallConfigs(cancellationToken);
        }
    }

    private void DeleteExistingFiles()
    {
        EmitStatus("Deleting existing files...");

        // Setup defaults, as per https://wiki.atlauncher.com/pack-admin/xml/delete
        var deletes = new VersionDeletes();
        deletes.Folders.Add(new VersionDelete { Base = "root", Target = "mods%s%" });
        deletes.Folders.Add(new VersionDelete { Base = "root", Target = "configs%s%" });
        deletes.Folders.Add(new VersionDelete { Base = "root", Target = "bin%s%" });

        // Setup defaults, as per https://wiki.atlauncher.com/pack-admin/xml/keep
        var keeps = new VersionKeeps();
        keeps.Files.Add(new VersionKeep { Base = "root", Target = "mods%s%PortalGunSounds.pak" });
        keeps.Folders.Add(new VersionKeep { Base = "root", Target = "mods%s%rei_minimap%s%" });
        keeps.Folders.Add(new VersionKeep { Base = "root", Target = "mods%s%VoxelMods%s%" });
        keeps.Files.Add(new VersionKeep { Base = "root", Target = "config%s%NEI.cfg" });
        keeps.Files.Add(new VersionKeep { Base = "root", Target = "options.txt" });
        keeps.Files.Add(new VersionKeep { Base = "root", Target = "servers.dat" });

        // Merge with version deletes and keeps
        deletes.Files.AddRange(_version.Deletes.Files);
        deletes.Folders.AddRange(_version.Deletes.Folders);
        keeps.Files.AddRange(_version.Keeps.Files);
        keeps.Folders.AddRange(_version.Keeps.Folders);

        string GetPathForBase(string baseName)
        {
            var minecraftPath = FileSystem.PathCombine(StagingPath, "minecraft");

            if (baseName == "root")
            {
                return minecraftPath;
            }
            if (baseName == "config")
            {
                return FileSystem.PathCombine(minecraftPath, "config");
            }
            return minecraftPath;
        }

        static string ConvertToSystemPath(string path)
        {
            return path.Replace("%s%", Path.DirectorySeparatorChar.ToString());
        }

        bool ShouldKeep(string fullPath)
        {
            foreach (var item in keeps.Files)
            {
                var path = FileSystem.PathCombine(GetPathForBase(item.Base), ConvertToSystemPath(item.Target));
                if (fullPath == path)
                {
                    return true;
                }
            }

            foreach (var item in keeps.Folders)
            {
                var path = FileSystem.PathCombine(GetPathForBase(item.Base), ConvertToSystemPath(item.Target));
                if (fullPath.StartsWith(path, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // Keep track of files to delete
        var filesToDelete = new HashSet<string>();

        foreach (var item in deletes.Files)
        {
            var fullPath = FileSystem.PathCombine(GetPathForBase(item.Base), ConvertToSystemPath(item.Target));

            if (ShouldKeep(fullPath))
            {
                continue;
            }

            filesToDelete.Add(fullPath);
        }

        foreach (var item in deletes.Folders)
        {
            var fullPath = FileSystem.PathCombine(GetPathForBase(item.Base), ConvertToSystemPath(item.Target));
            if (!Directory.Exists(fullPath))
            {
                continue;
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories))
            {
                if (ShouldKeep(path))
                {
                    continue;
                }

                filesToDelete.Add(path);
            }
        }

        // Delete the files
        foreach (var item in filesToDelete)
        {
            FileSystem.DeletePath(item);
        }
    }

    private string? GetDirForModType(ModType type, string raw)
    {
        switch (type)
        {
            // Mod types that can either be ignored at this stage, or ignored completely.
            case ModType.Root:
            case ModType.Extract:
            case ModType.Decomp:
            case ModType.TexturePackExtract:
            case ModType.ResourcePackExtract:
            case ModType.MCPC:
                return null;
            case ModType.Forge:
            // Forge detection happens later on, if it cannot be detected it will
            // install a jarmod component.
            case ModType.Jar:
                return "jarmods";
            case ModType.Mods:
                return "mods";
            case ModType.Flan:
                return "Flan";
            case ModType.Dependency:
                return FileSystem.PathCombine("mods", _version.Minecraft);
            case ModType.Ic2Lib:
                return FileSystem.PathCombine("mods", "ic2");
            case ModType.DenLib:
                return FileSystem.PathCombine("mods", "denlib");
            case ModType.Coremods:
                return "coremods";
            case ModType.Plugins:
                return "plugins";
            case ModType.TexturePack:
                return "texturepacks";
            case ModType.ResourcePack:
                return "resourcepacks";
            case ModType.ShaderPack:
                return "shaderpacks";
            case ModType.Millenaire:
                EmitStatus("Unsupported mod type: " + raw);
                return null;
            case ModType.Unknown:
                EmitFailed($"Unknown mod type: {raw}");
                return null;
            default:
                return null;
        }
    }

    private string? GetVersionForLoader(string uid)
    {
        var loader = _version.Loader;
        if (loader.Recommended || loader.Latest || loader.Choose)
        {
            if (loader.Recommended || loader.Latest)
            {
                var lookup = RecommendedLoaderVersionLookup;
                if (lookup is null)
                {
                    EmitFailed($"Failed to get local metadata index for {uid}");
                    return null;
                }

                // (the version-list traversal lives in the hook; forge/neoforge are filtered
                //  by the minecraft version, fabric is not)
                var found = lookup(uid, loader.Type, loader.Type != "fabric" ? _version.Minecraft : "");
                if (string.IsNullOrEmpty(found))
                {
                    EmitFailed($"Failed to find version for {loader.Type} loader");
                    return null;
                }
                return found;
            }
            if (loader.Choose)
            {
                // Fabric Loader doesn't depend on a given Minecraft version.
                return ChooseLoaderVersion?.Invoke(uid, loader.Type == "fabric" ? null : _version.Minecraft);
            }
        }

        if (string.IsNullOrEmpty(loader.Version))
        {
            EmitFailed("No loader version set for modpack!");
            return null;
        }

        return loader.Version;
    }

    private static string DetectLibrary(VersionLibrary library)
    {
        // Try to detect what the library is
        if (library.Server.Length != 0 && library.Server.Split('/').Length >= 3)
        {
            var lastSlash = library.Server.LastIndexOf('/');
            var locationAndVersion = library.Server[..lastSlash];
            var fileName = library.Server[(lastSlash + 1)..];

            lastSlash = locationAndVersion.LastIndexOf('/');
            var location = locationAndVersion[..lastSlash];
            var version = locationAndVersion[(lastSlash + 1)..];

            lastSlash = location.LastIndexOf('/');
            var group = location[..lastSlash].Replace('/', '.');
            var artefact = location[(lastSlash + 1)..];

            return group + ":" + artefact + ":" + version;
        }

        if (library.File.Contains('-'))
        {
            var lastSlash = library.File.LastIndexOf('-');
            var name = library.File[..lastSlash];
            var version = library.File[(lastSlash + 1)..].Replace(".jar", "");

            if (name == "guava")
            {
                return "com.google.guava:guava:" + version;
            }
            if (name == "commons-lang3")
            {
                return "org.apache.commons:commons-lang3:" + version;
            }
        }

        return "org.multimc.atlauncher:" + library.Md5 + ":1";
    }

    private bool CreateLibrariesComponent(string instanceRoot, PackProfile profile)
    {
        if (_version.Libraries.Count == 0)
        {
            return true;
        }

        var exempt = new List<GradleSpecifier>();
        foreach (var componentUid in _componentsToInstall.Keys)
        {
            var componentData = ComponentDataFor(componentUid);
            if (componentData is not null)
            {
                foreach (var library in componentData.Libraries)
                {
                    exempt.Add(library.RawName());
                }
            }
        }

        if (_minecraftVersionData is not null)
        {
            foreach (var library in _minecraftVersionData.Libraries)
            {
                exempt.Add(library.RawName());
            }
        }

        var id = Guid.NewGuid().ToString(); // QUuid::createUuid().toString(QUuid::WithoutBraces)
        var targetId = "org.multimc.atlauncher." + id;

        var patchDir = FileSystem.PathCombine(instanceRoot, "patches");
        if (!FileSystem.EnsureFolderPathExists(patchDir))
        {
            return false;
        }
        var patchFileName = FileSystem.PathCombine(patchDir, targetId + ".json");

        var f = new VersionFile
        {
            Name = PackName + " " + VersionName + " (libraries)",
        };

        foreach (var lib in _version.Libraries)
        {
            // If the library is LiteLoader, we need to ignore it and handle it separately.
            if (LiteLoaderMap.TryGetValue(lib.Md5, out var liteLoaderVersion))
            {
                var liteLoaderData = MetaVersionDataLookup?.Invoke("com.mumfrey.liteloader", liteLoaderVersion);
                if (liteLoaderData is not null)
                {
                    _componentsToInstall["com.mumfrey.liteloader"] = liteLoaderVersion;
                    continue;
                }
            }

            var libName = DetectLibrary(lib);
            var libSpecifier = new GradleSpecifier(libName);

            var libExempt = false;
            foreach (var existingLib in exempt)
            {
                if (libSpecifier.MatchName(existingLib))
                {
                    // If the pack specifies a newer version of the lib, use that!
                    libExempt = new McVersion(libSpecifier.Version).CompareTo(new McVersion(existingLib.Version)) >= 0;
                }
            }
            if (libExempt)
            {
                continue;
            }

            var library = new Library();
            library.SetRawName(new GradleSpecifier(libName));

            switch (lib.Download)
            {
                case DownloadType.Server:
                    library.SetAbsoluteUrl(AtLauncherApi.DownloadServerUrl + lib.Url);
                    break;
                case DownloadType.Direct:
                    library.SetAbsoluteUrl(lib.Url);
                    break;
                case DownloadType.Browser:
                case DownloadType.Unknown:
                    EmitFailed($"Unknown or unsupported download type: {lib.DownloadRaw}");
                    return false;
            }

            f.Libraries.Add(library);
        }

        if (f.Libraries.Count == 0)
        {
            return true;
        }

        WritePatchFile(patchFileName, f);
        profile.AppendComponent(new Component(profile, targetId, f));
        return true;
    }

    private bool CreatePackComponent(string instanceRoot, PackProfile profile)
    {
        if (_version.MainClass.MainClass.Length == 0 && _version.ExtraArguments.Arguments.Length == 0)
        {
            return true;
        }

        var mainClass = _version.MainClass.MainClass;
        var extraArguments = _version.ExtraArguments.Arguments;

        var hasMainClassDepends = _version.MainClass.Depends.Length != 0;
        var hasExtraArgumentsDepends = _version.ExtraArguments.Depends.Length != 0;
        if (hasMainClassDepends || hasExtraArgumentsDepends)
        {
            var mods = new HashSet<string>();
            foreach (var item in _version.Mods)
            {
                mods.Add(item.Name);
            }

            if (hasMainClassDepends && !mods.Contains(_version.MainClass.Depends))
            {
                mainClass = "";
            }

            if (hasExtraArgumentsDepends && !mods.Contains(_version.ExtraArguments.Depends))
            {
                extraArguments = "";
            }
        }

        if (mainClass.Length == 0 && extraArguments.Length == 0)
        {
            return true;
        }

        var id = Guid.NewGuid().ToString();
        var targetId = "org.multimc.atlauncher." + id;

        var patchDir = FileSystem.PathCombine(instanceRoot, "patches");
        if (!FileSystem.EnsureFolderPathExists(patchDir))
        {
            return false;
        }
        var patchFileName = FileSystem.PathCombine(patchDir, targetId + ".json");

        var mainClasses = new List<string>();
        var tweakers = new List<string>();
        foreach (var componentUid in _componentsToInstall.Keys)
        {
            var componentData = ComponentDataFor(componentUid);
            if (componentData is not null)
            {
                if (componentData.MainClass.Length != 0)
                {
                    mainClasses.Add(componentData.MainClass);
                }
                tweakers.AddRange(componentData.AddTweakers);
            }
        }

        var f = new VersionFile
        {
            Name = PackName + " " + VersionName,
        };
        if (mainClass.Length != 0 && !mainClasses.Contains(mainClass))
        {
            f.MainClass = mainClass;
        }
        var previous = "";
        foreach (var rawArg in extraArguments.Split(' '))
        {
            // NOTE: the C++ removes "--tweakClass=" before the startsWith check, so the
            // first condition is only true for a doubled prefix; the "--tweakClass
            // <class>" two-argument form does the real work (upstream quirk, ported).
            var arg = rawArg.Replace("--tweakClass=", "");
            if (arg.StartsWith("--tweakClass=", StringComparison.Ordinal) || previous == "--tweakClass")
            {
                if (!tweakers.Contains(arg))
                {
                    f.AddTweakers.Add(arg);
                }
            }
            previous = arg;
        }

        if (f.MainClass.Length == 0 && f.AddTweakers.Count == 0)
        {
            return true;
        }

        WritePatchFile(patchFileName, f);
        profile.AppendComponent(new Component(profile, targetId, f));
        return true;
    }

    /// <summary>The component data of a uid registered in _componentsToInstall (null when the
    /// meta lookup has no data — the C++ checks componentVersion->data() the same way).</summary>
    private VersionFile? ComponentDataFor(string uid)
    {
        return _componentsToInstall.TryGetValue(uid, out var version)
            ? MetaVersionDataLookup?.Invoke(uid, version)
            : null;
    }

    private static void WritePatchFile(string patchFileName, VersionFile f)
    {
        FileSystem.Write(patchFileName,
            System.Text.Encoding.UTF8.GetBytes(QtJson.ToIndentedString(OneSixVersionFormat.VersionFileToJson(f)) + "\n"));
    }

    private async Task InstallConfigs(CancellationToken cancellationToken)
    {
        EmitStatus("Downloading configs...");

        var path = $"Configs/{PackSafeName}/{VersionName}.zip";
        var url = AtLauncherApi.PackConfigsZipUrl(PackSafeName, VersionName);

        NetRequest dl;
        if (MetaCache is not null)
        {
            var entry = MetaCache.ResolveEntry("ATLauncherPacks", path);
            entry.SetStale(true);
            dl = NetRequest.MakeCached(MetaCache, new Uri(url), entry);
            _archivePath = entry.GetFullPath();
        }
        else
        {
            _archivePath = TempCachePath(path);
            dl = NetRequest.MakeFile(new Uri(url), _archivePath);
        }

        if (_version.Configs.Sha1.Length != 0)
        {
            dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.SHA1, _version.Configs.Sha1));
        }

        await dl.StartAsync(cancellationToken);
        if (!dl.WasSuccessful)
        {
            EmitFailed(dl.FailReason);
            return;
        }

        await ExtractConfigs(cancellationToken);
    }

    private async Task ExtractConfigs(CancellationToken cancellationToken)
    {
        EmitStatus("Extracting configs...");

        var extractDir = FileSystem.PathCombine(StagingPath, "minecraft");
        // (the C++ does not check the extraction result here — it proceeds to the mods)
        _ = MMCZip.ExtractDir(_archivePath, "", extractDir);

        await DownloadMods(cancellationToken);
    }

    private async Task DownloadMods(CancellationToken cancellationToken)
    {
        var optionalMods = new List<VersionMod>();
        foreach (var mod in _version.Mods)
        {
            if (mod.Optional)
            {
                optionalMods.Add(mod);
            }
        }

        // Select optional mods, if pack contains any
        var selectedMods = new List<string>();
        if (optionalMods.Count != 0)
        {
            EmitStatus("Selecting optional mods...");
            foreach (var mod in optionalMods)
            {
                if (OptionalModsSelection.TryGetValue(mod.Name, out var selected) && selected)
                {
                    selectedMods.Add(mod.Name);
                }
            }
            // (the C++ aborts when the dialog is rejected; the pre-set selection cannot be
            //  rejected, so an empty selection simply installs nothing)
        }

        EmitStatus("Downloading mods...");

        _jarMods.Clear();
        var modJob = new NetJob("Mod download");
        var downloadRequests = new List<NetRequest>();
        var blockedMods = new List<(VersionMod Mod, Flame.BlockedMod Blocked)>();

        foreach (var mod in _version.Mods)
        {
            // skip non-client mods
            if (!mod.Client)
            {
                continue;
            }

            // skip optional mods that were not selected
            if (mod.Optional && !selectedMods.Contains(mod.Name))
            {
                continue;
            }

            string url;
            switch (mod.Download)
            {
                case DownloadType.Server:
                    url = AtLauncherApi.DownloadServerUrl + mod.Url;
                    break;
                case DownloadType.Browser:
                    {
                        blockedMods.Add((mod, new Flame.BlockedMod
                        {
                            Name = mod.File,
                            WebsiteUrl = mod.Url,
                            Hash = mod.Md5,
                            Matched = false,
                            LocalPath = "",
                        }));
                        continue;
                    }
                case DownloadType.Direct:
                    url = mod.Url;
                    break;
                case DownloadType.Unknown:
                    EmitFailed($"Unknown download type: {mod.DownloadRaw}");
                    return;
                default:
                    EmitFailed($"Unknown download type: {mod.DownloadRaw}");
                    return;
            }

            var cacheName = Path.GetFileNameWithoutExtension(mod.File) + "-" + mod.Md5 + Path.GetExtension(mod.File);

            if (mod.Type is ModType.Extract or ModType.TexturePackExtract or ModType.ResourcePackExtract)
            {
                var cachePath = CachePathFor(cacheName, url, modJob, downloadRequests, mod);
                _modsToExtract[cachePath] = mod;
            }
            else if (mod.Type == ModType.Decomp)
            {
                var cachePath = CachePathFor(cacheName, url, modJob, downloadRequests, mod);
                _modsToDecomp[cachePath] = mod;
            }
            else
            {
                var relpath = GetDirForModType(mod.Type, mod.TypeRaw);
                if (relpath is null)
                {
                    if (State == TaskState.Failed)
                    {
                        return;
                    }
                    continue;
                }

                var cachePath = CachePathFor(cacheName, url, modJob, downloadRequests, mod);

                var path = FileSystem.PathCombine(StagingPath, "minecraft", relpath, mod.File);

                if (mod.Type == ModType.Forge)
                {
                    var forgeData = MetaVersionDataLookup?.Invoke("net.minecraftforge", mod.ModVersion);
                    if (forgeData is not null)
                    {
                        _componentsToInstall["net.minecraftforge"] = mod.ModVersion;
                        continue;
                    }

                    _jarMods.Add(path);
                }

                if (mod.Type == ModType.Jar)
                {
                    _jarMods.Add(path);
                }

                // Download after Forge handling, to avoid downloading Forge twice.
                _modsToCopy[cachePath] = path;
            }
        }

        if (blockedMods.Count != 0)
        {
            // (upstream opens the BlockedModsDialog here; the headless flow runs the optional
            //  local-file resolver instead and fails like the dialog's reject path)
            BlockedMods = blockedMods.Select(b => b.Blocked).ToList();

            var anyMatched = false;
            foreach (var (mod, blocked) in blockedMods)
            {
                var localPath = BlockedModLocalFileResolver?.Invoke(blocked);
                if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                {
                    blocked.Matched = true;
                    blocked.LocalPath = localPath;
                    anyMatched = true;

                    if (mod.Type is ModType.Extract or ModType.TexturePackExtract or ModType.ResourcePackExtract)
                    {
                        _modsToExtract[localPath] = mod;
                    }
                    else if (mod.Type == ModType.Decomp)
                    {
                        _modsToDecomp[localPath] = mod;
                    }
                    else
                    {
                        var relpath = GetDirForModType(mod.Type, mod.TypeRaw);
                        if (relpath is null)
                        {
                            if (State == TaskState.Failed)
                            {
                                return;
                            }
                            continue;
                        }

                        var path = FileSystem.PathCombine(StagingPath, "minecraft", relpath, mod.File);

                        if (mod.Type == ModType.Forge)
                        {
                            var forgeData = MetaVersionDataLookup?.Invoke("net.minecraftforge", mod.ModVersion);
                            if (forgeData is not null)
                            {
                                _componentsToInstall["net.minecraftforge"] = mod.ModVersion;
                                continue;
                            }

                            _jarMods.Add(path);
                        }

                        if (mod.Type == ModType.Jar)
                        {
                            _jarMods.Add(path);
                        }

                        _modsToCopy[localPath] = path;
                    }
                }
            }

            if (!anyMatched)
            {
                EmitFailed("The following files are not available for download in third party launchers.\n" +
                           "You will need to manually download them and add them to the instance:\n" +
                           string.Join("\n", BlockedMods.Select(m => $"{m.Name} ({m.WebsiteUrl})")) + "\n" +
                           "Unknown download type: browser");
                return;
            }
        }

        modJob.Progress += (_, _) =>
        {
            EmitDetails($"{downloadRequests.Count(t => t.IsFinished)} out of {downloadRequests.Count} complete");
            SetProgress(downloadRequests.Count(t => t.IsFinished), downloadRequests.Count);
        };
        await modJob.StartAsync(cancellationToken);
        if (!modJob.WasSuccessful)
        {
            EmitFailed(modJob.FailReason);
            return;
        }

        if (_modsToExtract.Count != 0 || _modsToDecomp.Count != 0 || _modsToCopy.Count != 0)
        {
            if (!ExtractMods())
            {
                if (State != TaskState.Failed)
                {
                    EmitFailed("Failed to extract mods...");
                }
                return;
            }
        }

        await Install(cancellationToken);
    }

    /// <summary>Queue a mod download; returns the local path the file lands in.</summary>
    private string CachePathFor(string cacheName, string url, NetJob modJob, List<NetRequest> downloadRequests,
        VersionMod mod)
    {
        string cachePath;
        NetRequest dl;
        if (MetaCache is not null)
        {
            var entry = MetaCache.ResolveEntry("ATLauncherPacks", cacheName);
            entry.SetStale(true);
            cachePath = entry.GetFullPath();
            dl = NetRequest.MakeCached(MetaCache, new Uri(url), entry);
        }
        else
        {
            cachePath = TempCachePath(cacheName);
            dl = NetRequest.MakeFile(new Uri(url), cachePath);
        }

        if (mod.Md5.Length != 0)
        {
            dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.MD5, mod.Md5));
        }
        modJob.AddNetRequest(dl);
        downloadRequests.Add(dl);
        return cachePath;
    }

    private static string TempCachePath(string relativeName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flint-atl-cache");
        FileSystem.EnsureFolderPathExists(dir);
        return FileSystem.PathCombine(dir, relativeName.Replace("%s%", "_").Replace('/', '_'));
    }

    private bool ExtractMods()
    {
        EmitStatus("Extracting mods...");
        foreach (var (modPath, mod) in _modsToExtract)
        {
            string extractToDir;
            if (mod.Type == ModType.Extract)
            {
                var dir = GetDirForModType(mod.ExtractTo, mod.ExtractToRaw);
                if (dir is null)
                {
                    return false;
                }
                extractToDir = dir;
            }
            else if (mod.Type == ModType.TexturePackExtract)
            {
                extractToDir = FileSystem.PathCombine("texturepacks", "extracted");
            }
            else if (mod.Type == ModType.ResourcePackExtract)
            {
                extractToDir = FileSystem.PathCombine("resourcepacks", "extracted");
            }
            else
            {
                continue;
            }

            var extractToPath = FileSystem.PathCombine(StagingPath, "minecraft", extractToDir);

            var folderToExtract = "";
            if (mod.Type == ModType.Extract)
            {
                folderToExtract = mod.ExtractFolder;
                folderToExtract = LeadingSlashRegex().Replace(folderToExtract, "");
                if (IsPathTraversal(extractToPath, folderToExtract))
                {
                    EmitStatus("Blocked path traversal in " + mod.ExtractFolder);
                    return false;
                }
            }

            if (MMCZip.ExtractDir(modPath, folderToExtract, extractToPath) is null)
            {
                // assume error
                return false;
            }
        }

        foreach (var (modPath, mod) in _modsToDecomp)
        {
            var dir = GetDirForModType(mod.DecompType, mod.DecompTypeRaw);
            if (dir is null)
            {
                return false;
            }
            var extractToPath = FileSystem.PathCombine(StagingPath, "minecraft", dir, mod.DecompFile);

            if (IsPathTraversal(FileSystem.PathCombine(StagingPath, "minecraft", dir), mod.DecompFile))
            {
                EmitStatus("Blocked path traversal in decompFile " + mod.DecompFile);
                return false;
            }

            if (!MMCZip.ExtractFile(modPath, mod.DecompFile, extractToPath))
            {
                return false;
            }
        }

        foreach (var (from, to) in _modsToCopy)
        {
            // If the file already exists, assume the mod is the correct copy - and remove
            // the copy from the Configs.zip
            if (File.Exists(to) && !FileSystem.DeletePath(to))
            {
                EmitStatus("Failed to delete " + to);
                return false;
            }

            if (!File.Exists(from) || !FileSystem.Copy(from, to))
            {
                EmitStatus($"Failed to copy {from} to {to}");
                return false;
            }
        }
        return true;
    }

    private async Task Install(CancellationToken cancellationToken)
    {
        EmitStatus("Installing modpack");

        var instanceConfigPath = FileSystem.PathCombine(StagingPath, "instance.cfg");
        var instance = new MinecraftInstance(
            GlobalSettings ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentSettings before execution."),
            new Settings.IniSettingsObject(instanceConfigPath), StagingPath);
        {
            var components = instance.GetPackProfile();
            components.BuildingFromScratch();

            // Use a component to add libraries BEFORE Minecraft
            if (!CreateLibrariesComponent(instance.InstanceRoot(), components))
            {
                EmitFailed("Failed to create libraries component");
                return;
            }

            // Minecraft
            await components.SetComponentVersion("net.minecraft", _version.Minecraft, important: true, cancellationToken);

            // Loader
            if (_version.Loader.Type == "forge")
            {
                var version = GetVersionForLoader("net.minecraftforge");
                if (version is null)
                {
                    return;
                }

                await components.SetComponentVersion("net.minecraftforge", version, cancellationToken: cancellationToken);
            }
            else if (_version.Loader.Type == "neoforge")
            {
                var version = GetVersionForLoader("net.neoforged");
                if (version is null)
                {
                    return;
                }

                await components.SetComponentVersion("net.neoforged", version, cancellationToken: cancellationToken);
            }
            else if (_version.Loader.Type == "fabric")
            {
                var version = GetVersionForLoader("net.fabricmc.fabric-loader");
                if (version is null)
                {
                    return;
                }

                await components.SetComponentVersion("net.fabricmc.fabric-loader", version, cancellationToken: cancellationToken);
            }
            else if (_version.Loader.Type.Length != 0)
            {
                EmitFailed("Unknown loader type: " + _version.Loader.Type);
                return;
            }

            foreach (var (componentUid, componentVersion) in _componentsToInstall)
            {
                await components.SetComponentVersion(componentUid, componentVersion, cancellationToken: cancellationToken);
            }

            _ = components.InstallJarMods(_jarMods);

            // Use a component to fill in the rest of the data
            // todo: use more detection
            if (!CreatePackComponent(instance.InstanceRoot(), components))
            {
                EmitFailed("Failed to create pack component");
                return;
            }

            components.SaveNow();

            instance.SetName(Name());
            instance.SetIconKey(Icon);
            instance.SetManagedPack("atlauncher", PackSafeName, PackName, VersionName, VersionName);

            _jarMods.Clear();
        }

        // (upstream calls downloadFiles(m_instance.get()) here — the P5 update pipeline;
        //  like the other creation tasks, creation succeeds without fetching game files)
        await Task.CompletedTask;
        EmitSucceeded();
    }

    /// <summary>Port of the isPathTraversal helper.</summary>
    private static bool IsPathTraversal(string basePath, string entryName)
    {
        var safeName = FileSystem.RemoveInvalidPathChars(entryName);
        var fullPath = FileSystem.PathCombine(basePath, safeName);
        var fullRoot = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseRoot = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullRoot != baseRoot && !fullRoot.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, string> LiteLoaderMap = new()
    {
        ["61179803bcd5fb7790789b790908663d"] = "1.12-SNAPSHOT",
        ["1420785ecbfed5aff4a586c5c9dd97eb"] = "1.12.2-SNAPSHOT",
        ["073f68e2fcb518b91fd0d99462441714"] = "1.6.2_03",
        ["10a15b52fc59b1bfb9c05b56de1097d6"] = "1.6.2_02",
        ["b52f90f08303edd3d4c374e268a5acf1"] = "1.6.2_04",
        ["ea747e24e03e24b7cad5bc8a246e0319"] = "1.6.2_01",
        ["55785ccc82c07ff0ba038fe24be63ea2"] = "1.7.10_01",
        ["63ada46e033d0cb6782bada09ad5ca4e"] = "1.7.10_04",
        ["7983e4b28217c9ae8569074388409c86"] = "1.7.10_03",
        ["c09882458d74fe0697c7681b8993097e"] = "1.7.10_02",
        ["db7235aefd407ac1fde09a7baba50839"] = "1.7.10_00",
        ["6e9028816027f53957bd8fcdfabae064"] = "1.8",
        ["5e732dc446f9fe2abe5f9decaec40cde"] = "1.10-SNAPSHOT",
        ["3a98b5ed95810bf164e71c1a53be568d"] = "1.11.2-SNAPSHOT",
        ["ba8e6285966d7d988a96496f48cbddaa"] = "1.8.9-SNAPSHOT",
        ["8524af3ac3325a82444cc75ae6e9112f"] = "1.11-SNAPSHOT",
        ["53639d52340479ccf206a04f5e16606f"] = "1.5.2_01",
        ["1fcdcf66ce0a0806b7ad8686afdce3f7"] = "1.6.4_00",
        ["531c116f71ae2b11033f9a11a0f8e668"] = "1.6.4_01",
        ["4009eeb99c9068f608d3483a6439af88"] = "1.7.2_03",
        ["66f343354b8417abce1a10d557d2c6e9"] = "1.7.2_04",
        ["ab554c21f28fbc4ae9b098bcb5f4cceb"] = "1.7.2_05",
        ["e1d76a05a3723920e2f80a5e66c45f16"] = "1.7.2_02",
        ["00318cb0c787934d523f63cdfe8ddde4"] = "1.9-SNAPSHOT",
        ["986fd1ee9525cb0dcab7609401cef754"] = "1.9.4-SNAPSHOT",
        ["571ad5e6edd5ff40259570c9be588bb5"] = "1.9.4",
        ["1cdd72f7232e45551f16cc8ffd27ccf3"] = "1.10.2-SNAPSHOT",
        ["8a7c21f32d77ee08b393dd3921ced8eb"] = "1.10.2",
        ["b9bef8abc8dc309069aeba6fbbe58980"] = "1.12.1-SNAPSHOT",
    };

    [GeneratedRegex("^/")]
    private static partial Regex LeadingSlashRegex();
}
