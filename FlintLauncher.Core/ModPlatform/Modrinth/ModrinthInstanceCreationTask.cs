// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/modrinth/ModrinthInstanceCreationTask.{h,cpp}: unpack a .mrpack into
// a new instance (staging) folder and download the indexed files.
//
// The exact override-dir mapping of the C++ is kept:
//   - 'overrides'        -> moved to the instance's minecraft dir (the game root)
//   - 'client-overrides' -> copied over the game root (FS::overrideFolder semantics)
//   - (server overrides are not part of the format's import path)
//   - modrinth.index.json is kept inside mrpack/ for later version changes
// The mod files land under the game root, keeping their index paths ("mods/...", ...);
// every downloaded file is checksum-verified (sha512) and mods receive their .pw.toml
// metadata through EnsureMetadataTask.
//
// Deviations (headless port):
//  - The update-existing-instance flow of executeTask() (APPLICATION->instances() lookups,
//    the askIfShouldUpdate dialog and the old-mrpack diffing) is deferred to P6-c; the task
//    goes straight to createInstance().
//  - The optional-mods dialog: optional files are installed disabled (".disabled" suffix),
//    like the C++ non-dialog path.
//  - promptForUntrustedMods: without the UntrustedModsDialog the install fails listing the
//    untrusted files when TrustedSource is false (the C++ dialog defaults to rejecting).
//  - Downloads run through a ConcurrentTask of per-file tasks so the fallback-URL retry can
//    mark the file failed only after every URL was tried (the C++ chains new net actions
//    from the failed signal).

using System.Security.Cryptography;
using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthCreationTask : InstanceTask
{
    public sealed class File
    {
        public string Path { get; set; } = "";

        public HashAlgorithmName HashAlgorithm { get; set; } = HashAlgorithmName.SHA512;

        /// <summary>Hex-encoded hash, as stored in modrinth.index.json.</summary>
        public string Hash { get; set; } = "";

        public Queue<string> Downloads { get; } = new();
        public bool Required { get; set; } = true;
    }

    public ModrinthCreationTask(string stagingPath,
        bool trustedSource,
        InstanceEnvironment environment,
        string id = "",
        string versionId = "",
        string originalInstanceId = "",
        ModrinthApi? modrinth = null)
    {
        _modrinth = modrinth ?? new ModrinthApi();
        StagingPath = stagingPath;
        TrustedSource = trustedSource;
        SetParentEnvironment(environment);
        _managedId = id;
        _managedVersionId = versionId;
        SetOverride(false, originalInstanceId);
    }

    public bool TrustedSource { get; }

    private readonly ModrinthApi _modrinth;
    private string _minecraftVersion = "";
    private string _fabricVersion = "";
    private string _quiltVersion = "";
    private string _forgeVersion = "";
    private string _neoForgeVersion = "";
    private readonly string _managedId;
    private string _managedVersionId;
    private string _managedName = "";

    private readonly List<File> _files = [];
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.Ordinal);

    private readonly string _rootPath = "minecraft";

    /// <summary>The untrusted files reported when TrustedSource is false (see header note).</summary>
    public IReadOnlyList<string> UntrustedMods { get; private set; } = [];

    /// <summary>
    /// Headless stand-in for the OptionalModDialog selection (fix-2d): keys are the manifest
    /// file paths ("mods/..."), values the selected flag. Selected optional files install
    /// enabled (like the C++ dialog's "required" upgrade), everything else keeps the
    /// non-dialog behavior (".disabled" suffix). Flagged Core addition: the C++ gets this
    /// from the dialog, so the property does not exist upstream.
    /// </summary>
    public IReadOnlyDictionary<string, bool> OptionalModsSelection { get; set; } =
        new Dictionary<string, bool>();

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // (upstream checks for an existing instance to update here — deferred, see header)
        await CreateInstanceAsync(cancellationToken);
    }

    // https://docs.modrinth.com/docs/modpacks/format_definition/
    private async Task CreateInstanceAsync(CancellationToken cancellationToken)
    {
        var parentFolder = FileSystem.PathCombine(StagingPath, "mrpack");

        var indexPath = FileSystem.PathCombine(StagingPath, "modrinth.index.json");
        if (_files.Count == 0 && !ParseManifest(indexPath, _files, setInternalData: true))
        {
            return;
        }

        // Keep index file in case we need it some other time (like when changing versions)
        var newIndexPlace = FileSystem.PathCombine(parentFolder, "modrinth.index.json");
        FileSystem.EnsureFilePathExists(newIndexPlace);
        FileSystem.Move(indexPath, newIndexPlace);

        var mcPath = FileSystem.PathCombine(StagingPath, _rootPath);

        var overridePath = FileSystem.PathCombine(StagingPath, "overrides");
        if (System.IO.File.Exists(overridePath) || Directory.Exists(overridePath))
        {
            // Create a list of overrides in "overrides.txt" inside mrpack/
            OverrideUtils.CreateOverrides("overrides", parentFolder, overridePath);

            // Apply the overrides
            if (!FileSystem.Move(overridePath, mcPath))
            {
                EmitFailed("Could not rename the overrides folder:\n" + "overrides");
                return;
            }
        }

        // Do client overrides
        var clientOverridePath = FileSystem.PathCombine(StagingPath, "client-overrides");
        if (System.IO.File.Exists(clientOverridePath) || Directory.Exists(clientOverridePath))
        {
            // Create a list of overrides in "client-overrides.txt" inside mrpack/
            OverrideUtils.CreateOverrides("client-overrides", parentFolder, clientOverridePath);

            // Apply the overrides
            if (!OverrideUtils.OverrideFolder(mcPath, clientOverridePath))
            {
                EmitFailed("Could not rename the client overrides folder:\n" + "client overrides");
                return;
            }
        }

        if (!PromptForUntrustedMods())
        {
            EmitFailed("The pack contains files from untrusted sources:\n" + string.Join("\n", UntrustedMods));
            return;
        }

        // D4: the staged instance is Flint format from birth (typed instance.json adapter)
        var newInstance = new MinecraftInstance(
            new InstanceJsonSettingsObject(StagingPath, Environment ??
                                                        throw new InvalidOperationException(
                                                            "InstanceTask requires SetParentEnvironment before execution.")),
            StagingPath);

        var components = newInstance.GetPackProfile();
        components.BuildingFromScratch();
        await components.SetComponentVersion("net.minecraft", _minecraftVersion, important: true, cancellationToken);

        string loader = "";
        if (_fabricVersion.Length != 0)
        {
            await components.SetComponentVersion("net.fabricmc.fabric-loader", _fabricVersion, important: false, cancellationToken);
            loader = ModIndex.GetModLoaderAsString(ModLoaderType.Fabric);
        }

        if (_quiltVersion.Length != 0)
        {
            await components.SetComponentVersion("org.quiltmc.quilt-loader", _quiltVersion, important: false, cancellationToken);
            loader = ModIndex.GetModLoaderAsString(ModLoaderType.Quilt);
        }

        if (_forgeVersion.Length != 0)
        {
            await components.SetComponentVersion("net.minecraftforge", _forgeVersion, important: false, cancellationToken);
            loader = ModIndex.GetModLoaderAsString(ModLoaderType.Forge);
        }

        if (_neoForgeVersion.Length != 0)
        {
            await components.SetComponentVersion("net.neoforged", _neoForgeVersion, important: false, cancellationToken);
            loader = ModIndex.GetModLoaderAsString(ModLoaderType.NeoForge);
        }

        if (Icon != "default")
        {
            newInstance.SetIconKey(Icon);
        }
        else if (_managedId.Length != 0)
        {
            newInstance.SetIconKey("modrinth");
        }

        SetManagedPack(newInstance);

        newInstance.SetName(Name());
        newInstance.SaveNow();

        // ---------------------------------------------------------- download mods
        var rootModpackPath = FileSystem.PathCombine(StagingPath, _rootPath);

        var downloadJob = new ConcurrentTask("Mod Download Modrinth");
        var downloadTasks = new List<ModDownloadTask>();

        foreach (var file in _files)
        {
            var fileName = FileSystem.RemoveInvalidPathChars(file.Path);
            var filePath = FileSystem.PathCombine(rootModpackPath, fileName);
            if (!IsParentOf(rootModpackPath, filePath))
            {
                // This means we somehow got out of the root folder, so abort here to prevent exploits
                EmitFailed(
                    $"One of the files has a path that leads to an arbitrary location ({fileName}). This is a security risk and isn't allowed.");
                return;
            }

            if (fileName.StartsWith("mods/", StringComparison.Ordinal))
            {
                var mod = new Mod(filePath);
                var d = new ModDetails
                {
                    ModId = filePath,
                };
                mod.SetDetails(d);
                _resources[file.Hash] = mod;
            }

            if (file.Downloads.Count == 0)
            {
                EmitFailed($"The file '{fileName}' is missing a download link. This is invalid in the pack format.");
                return;
            }

            var meta = new ModrinthApi.DownloadMeta(Reason: "modpack", GameVersion: _minecraftVersion, Loader: loader,
                DependentOn: _managedId.Length != 0 ? _managedVersionId : "");

            var downloadTask = new ModDownloadTask(file, filePath, meta, _modrinth);
            downloadTasks.Add(downloadTask);
            downloadJob.AddTask(downloadTask);
        }

        EmitStatus("Downloading mods...");
        downloadJob.Progress += (_, _) =>
        {
            EmitDetails($"{downloadTasks.Count(t => t.IsFinished)} out of {downloadTasks.Count} complete");
            SetProgress(downloadTasks.Count(t => t.IsFinished), downloadTasks.Count);
        };
        await downloadJob.StartAsync(cancellationToken);
        if (!downloadJob.WasSuccessful)
        {
            EmitFailed(downloadJob.FailReason);
            return;
        }

        await EnsureMetaLoop(cancellationToken, newInstance);
    }

    /// <summary>
    /// Port of ModrinthCreationTask::parseManifest: read modrinth.index.json. Returns false
    /// (after a failed emit) when the index could not be understood.
    /// </summary>
    internal bool ParseManifest(string indexPath, List<File> files, bool setInternalData = true,
        bool showOptionalDialog = false)
    {
        try
        {
            MrPackIndexJson? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(System.IO.File.ReadAllText(indexPath),
                    MrPackJsonContext.Default.MrPackIndexJson);
            }
            catch (JsonException e)
            {
                throw new JsonParseException("modrinth.index.json: Error parsing JSON: " + e.Message);
            }

            var obj = parsed
                      ?? throw new JsonParseException("modrinth.index.json: Error parsing JSON: document is null");
            var formatVersion = obj.FormatVersion
                                ?? throw new JsonParseException("modrinth.index.json is not an integer");
            if (formatVersion == 1)
            {
                var game = obj.Game ?? throw new JsonParseException("modrinth.index.json is not a string");
                if (game != "minecraft")
                {
                    throw new JsonParseException("Unknown game: " + game);
                }

                if (setInternalData)
                {
                    if (_managedVersionId.Length == 0)
                    {
                        _managedVersionId = obj.VersionId ?? "";
                    }

                    _managedName = obj.Name ?? "";
                }

                var jsonFiles = obj.Files ?? throw new JsonParseException("'files' is not an array");
                var optionalFiles = new List<File>();
                foreach (var modInfo in jsonFiles)
                {
                    var file = new File
                    {
                        Path = ModrinthRequire.String(modInfo.Path, "path").Replace("\\", "/"),
                    };

                    // 'env' field is optional (an object with no members behaves like an absent one)
                    if (modInfo.Env is { } env && (env.Client is not null || env.Server is not null))
                    {
                        var support = env.Client ?? "unsupported";
                        if (support == "unsupported")
                        {
                            continue;
                        }

                        if (support == "optional")
                        {
                            file.Required = false;
                        }
                    }

                    var hashes = ModrinthRequire.Object(modInfo.Hashes, "hashes");
                    file.Hash = hashes.TryGetValue("sha512", out var sha512)
                        ? sha512
                        : throw new JsonParseException("'sha512' is not a string");
                    file.HashAlgorithm = HashAlgorithmName.SHA512;

                    // Do not use a strict URL parser (the C++ uses QUrl's default TolerantMode,
                    // as Modrinth seems to incorrectly handle spaces)
                    if (modInfo.Downloads is { } downloadList)
                    {
                        for (var i = 0; i < downloadList.Count; i++)
                        {
                            var isLast = i == downloadList.Count - 1;

                            var downloadUrl = downloadList[i];
                            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var url))
                            {
                                if (isLast && file.Downloads.Count == 0)
                                {
                                    throw new JsonParseException($"Download URL for {file.Path} is not a correctly formatted URL");
                                }
                            }
                            else
                            {
                                file.Downloads.Enqueue(url.ToString());
                            }
                        }
                    }

                    (file.Required ? files : optionalFiles).Add(file);
                }

                if (optionalFiles.Count != 0)
                {
                    if (showOptionalDialog)
                    {
                        // (the C++ opens the OptionalModDialog here — headless port installs
                        //  optional files disabled instead, see the header note)
                        EmitAborted();
                        return false;
                    }

                    foreach (var file in optionalFiles)
                    {
                        if (OptionalModsSelection.TryGetValue(file.Path, out var selected) && selected)
                        {
                            // the OptionalModDialog's selection: selected optional files are
                            // upgraded to required (installed under their real path)
                            file.Required = true;
                        }
                        else
                        {
                            file.Path += ".disabled";
                        }

                        files.Add(file);
                    }
                }

                if (setInternalData)
                {
                    var dependencies = obj.Dependencies
                                       ?? throw new JsonParseException("'dependencies' is not an object");
                    foreach (var (name, dependency) in dependencies)
                    {
                        if (name == "minecraft")
                        {
                            _minecraftVersion = dependency
                                ?? throw new JsonParseException("Minecraft version is not a string");
                        }
                        else if (name == "fabric-loader")
                        {
                            _fabricVersion = dependency
                                ?? throw new JsonParseException("Fabric Loader version is not a string");
                        }
                        else if (name == "quilt-loader")
                        {
                            _quiltVersion = dependency
                                ?? throw new JsonParseException("Quilt Loader version is not a string");
                        }
                        else if (name == "forge")
                        {
                            _forgeVersion = dependency
                                ?? throw new JsonParseException("Forge version is not a string");
                        }
                        else if (name == "neoforge")
                        {
                            _neoForgeVersion = dependency
                                ?? throw new JsonParseException("NeoForge version is not a string");
                        }
                        else
                        {
                            throw new JsonParseException("Unknown dependency type: " + name);
                        }
                    }
                }
            }
            else
            {
                throw new JsonParseException($"Unknown format version: {formatVersion}");
            }
        }
        catch (JsonParseException e)
        {
            EmitFailed("Could not understand pack index:\n" + e.Message);
            return false;
        }

        return true;
    }

    private async Task EnsureMetaLoop(CancellationToken cancellationToken, MinecraftInstance newInstance)
    {
        var folder = FileSystem.PathCombine(newInstance.ModsRoot(), ".index");
        var ensureMetadataTask = new EnsureMetadataTask(_resources, folder, ResourceProvider.Modrinth, _modrinth)
        {
            // setUpdateLock(true): updates for mods coming from an installed modpack are locked
            LockUpdate = true
        };
        ensureMetadataTask.MetadataFailed += (_, reason) => EmitStatus(reason);
        EmitStatus("Generating metadata for mods...");
        await ensureMetadataTask.StartAsync(cancellationToken);
        if (!ensureMetadataTask.WasSuccessful)
        {
            EmitFailed(ensureMetadataTask.FailReason);
            return;
        }

        await FinishInstallAsync(newInstance);
    }

    /// <summary>
    /// Port of ModrinthCreationTask::promptForUntrustedMods: gather mods whose download
    /// URLs are not https+cdn or that exist as plain files in the pack. The C++ dialog
    /// becomes a reported failure (see the header note).
    /// </summary>
    private bool PromptForUntrustedMods()
    {
        if (TrustedSource)
        {
            return true;
        }

        var untrustedMods = new List<string>();

        foreach (var file in _files)
        {
            foreach (var url in file.Downloads)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https"
                                                                          || parsed.Host != _modrinth.DownloadHost)
                {
                    untrustedMods.Add(file.Path);
                    break;
                }
            }
        }

        var mcDir = FileSystem.PathCombine(StagingPath, _rootPath);
        var modsPath = FileSystem.PathCombine(mcDir, "mods");
        if (Directory.Exists(modsPath))
        {
            foreach (var iter in Directory.EnumerateFiles(modsPath, "*", SearchOption.AllDirectories))
            {
                untrustedMods.Add(Path.GetRelativePath(mcDir, iter).Replace('\\', '/'));
            }
        }

        if (untrustedMods.Count == 0)
        {
            return true;
        }

        UntrustedMods = untrustedMods;
        return false;
    }

    private void SetManagedPack(MinecraftInstance instance)
    {
        // Don't add managed info to packs without an ID (most likely imported from ZIP)
        if (_managedId.Length != 0)
        {
            instance.SetManagedPack("modrinth", _managedId, _managedName, _managedVersionId, Version());
        }
        else
        {
            instance.SetManagedPack("modrinth", "", Name(), "", "");
        }
    }

    private async Task FinishInstallAsync(MinecraftInstance newInstance)
    {
        // (upstream updates the old instance's name/managed info here — the update flow is
        //  deferred, see the header note)

        if (ShouldOverride)
        {
            var deleteFailed = false;

            EmitStatus("Removing old conflicting files...");

            foreach (var path in FilesToRemove)
            {
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                if (!FileSystem.DeletePath(path))
                {
                    deleteFailed = true;
                }
            }

            if (deleteFailed)
            {
                EmitFailed("Failed to remove old conflicting files.");
                return;
            }
        }

        _ = newInstance;
        // (upstream calls downloadFiles(m_newInstance.get()) here — the P5 update pipeline;
        //  like VanillaCreationTask, creation succeeds without fetching game files)
        EmitSucceeded();
    }

    /// <summary>QUrl::fromLocalFile(...).isParentOf(...) equivalent for local paths.</summary>
    private static bool IsParentOf(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent);
        var childFull = Path.GetFullPath(child);
        if (!childFull.StartsWith(parentFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }

        return childFull.Length == parentFull.Length
               || childFull[parentFull.Length] == Path.DirectorySeparatorChar
               || childFull[parentFull.Length] == '/';
    }

    /// <summary>
    /// One indexed file: try every download URL in order, checksum-verified (the per-file
    /// slice of the C++ download NetJob with its failed -> next-URL fallback chain).
    /// </summary>
    private sealed class ModDownloadTask(File file, string filePath, ModrinthApi.DownloadMeta meta, ModrinthApi modrinth)
        : FlintTask
    {
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            FileSystem.EnsureFilePathExists(filePath);
            var hashAlgorithm = file.HashAlgorithm;

            while (file.Downloads.Count > 0)
            {
                var url = file.Downloads.Dequeue();
                EmitStatus($"Downloading {file.Path}...");
                var dl = modrinth.MakeFileRequest(url, filePath, meta);
                dl.AddValidator(ChecksumValidator.FromHex(hashAlgorithm, file.Hash));
                await dl.StartAsync(cancellationToken);
                if (dl.WasSuccessful)
                {
                    EmitSucceeded();
                    return;
                }

                if (!IsRunning)
                {
                    return; // aborted
                }
                // else: try the next URL
            }

            EmitFailed($"Failed to download {file.Path}: {file.Hash}");
        }
    }
}
