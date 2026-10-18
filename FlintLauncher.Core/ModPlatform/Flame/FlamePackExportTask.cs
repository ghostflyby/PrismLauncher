// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/flame/FlamePackExportTask.{h,cpp}: export an instance to a CurseForge
// zip (manifest.json + modlist.html + an overrides/ zip). Mods resolved from local Flame
// packwiz metadata (project-id/file-id) or from the API by murmur2 fingerprint are listed
// as manifest files and excluded from the overrides; everything else is packed.
//
// The "missing metadata" behavior of the C++ is kept: mods without Flame metadata are
// fingerprinted through POST /fingerprints; files that stay unresolved are packed into
// overrides/ and simply don't appear in the manifest's files list.
//
// Deviations (headless port):
//  - loaderModList() is not part of the P3b MinecraftInstance port; the task builds its own
//    ModFolderModel over instance.ModsRoot() and uses ModFolderModel.GetMetadata for the
//    local-metadata resolution.
//  - The ExportToZipTask becomes direct ZipArchive writing with the same layout
//    ("overrides/" prefix, manifest.json/modlist.html as extra entries, resolved files
//    excluded).
//  - The progress/step plumbing of the Qt signals is reduced to status/progress updates.
//  - The per-mod authors of the local-metadata path come from the parsed mod details
//    (Mod::authors()); the API-filled path takes them from the project response.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Flame;

using ResourceFolderType = Minecraft.Resources.ResourceType;

public sealed class FlamePackExportTask(FlamePackExportTask.Options options) : FlintTask
{
    private const string Template = "<li><a href=\"{url}\">{name}{authors}</a></li>\n";
    private static readonly string[] FileExtensions = ["jar", "zip"];

    /// <summary>Port of FlamePackExportOptions.</summary>
    public sealed class Options
    {
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required string Author { get; init; }
        public bool OptionalFiles { get; init; }
        public required MinecraftInstance Instance { get; init; }
        public required string Output { get; init; }
        public MmcZip.FilterFunction? Filter { get; init; }
        public int RecommendedRam { get; init; }
        public FlameApi? Flame { get; init; }
    }

    private sealed class ResolvedFile
    {
        public long AddonId { get; init; }
        public long Version { get; init; }
        public bool Enabled { get; init; }
        public bool IsMod { get; init; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Authors { get; set; } = "";
    }

    private sealed class HashInfo
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public bool Enabled { get; init; }
        public bool IsMod { get; init; }
    }

    private readonly string _gameRoot = options.Instance.GameRoot();
    private readonly FlameApi _flame = options.Flame ?? new FlameApi();

    private readonly List<string> _files = [];
    private readonly Dictionary<string, HashInfo> _pendingHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedFile> _resolvedFiles = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Searching for files...");
        SetProgress(0, 5);

        if (!CollectFiles())
        {
            return;
        }

        await CollectHashes();
        await MakeApiRequest(cancellationToken);
    }

    private bool CollectFiles()
    {
        _files.Clear();
        if (!MmcZip.CollectFileListRecursively(options.Instance.GameRoot(), null, _files,
                options.Filter is null
                    ? null
                    : (Func<FileSystemInfo, bool>)(f => options.Filter(GetRelativePath(f.FullName)))))
        {
            EmitFailed("Could not search for files");
            return false;
        }

        return true;
    }

    private async Task CollectHashes()
    {
        await Task.CompletedTask; // keep the async shape of the C++ callback chain

        EmitStatus("Finding file hashes...");
        SetProgress(1, 5);

        // instance->loaderModList()->update()
        var modList = new ModFolderModel(options.Instance.ModsRoot());
        modList.Update();
        var allMods = modList.AllMods();

        foreach (var file in _files)
        {
            var relative = GetRelativePath(file);
            // require sensible file types
            var sensibleType = FileExtensions.Any(extension =>
                relative.EndsWith('.' + extension, StringComparison.Ordinal)
                || relative.EndsWith('.' + extension + ".disabled", StringComparison.Ordinal));
            if (!sensibleType)
            {
                continue;
            }

            if (relative.StartsWith("resourcepacks/", StringComparison.Ordinal)
                && (relative.EndsWith(".zip", StringComparison.Ordinal)
                    || relative.EndsWith(".zip.disabled", StringComparison.Ordinal)))
            {
                // is resourcepack
                var hash = HashUtils.HashFile(file, Algorithm.Murmur2);
                _pendingHashes[hash] = new HashInfo
                {
                    Name = relative,
                    Path = file,
                    Enabled = relative.EndsWith(".zip", StringComparison.Ordinal),
                    IsMod = false,
                };
                continue;
            }

            var mod = allMods.Find(m => string.Equals(m.Fileinfo.FullName, file, StringComparison.Ordinal));
            if (mod is not null)
            {
                if (mod.Type == ResourceFolderType.Folder)
                {
                    continue;
                }

                var metadata = modList.GetMetadata(mod);
                if (metadata is not null && metadata.Provider == ResourceProvider.Flame)
                {
                    _resolvedFiles[file] = new ResolvedFile
                    {
                        AddonId = ParseLong(metadata.ProjectId),
                        Version = ParseLong(metadata.FileId),
                        Enabled = mod.Enabled,
                        IsMod = true,
                        Name = metadata.Name,
                        Slug = metadata.Slug,
                        Authors = string.Join(", ", mod.Authors()),
                    };
                    continue;
                }

                var modHash = HashUtils.HashFile(file, Algorithm.Murmur2);
                _pendingHashes[modHash] = new HashInfo
                {
                    Name = mod.Name,
                    Path = file,
                    Enabled = mod.Enabled,
                    IsMod = true,
                };
            }
        }
    }

    private async Task MakeApiRequest(CancellationToken cancellationToken)
    {
        if (_pendingHashes.Count == 0)
        {
            await BuildZipAsync(cancellationToken);
            return;
        }

        EmitStatus("Finding versions for hashes...");
        SetProgress(2, 5);

        var fingerprints = new List<uint>();
        foreach (var murmur in _pendingHashes.Keys)
        {
            if (uint.TryParse(murmur, NumberStyles.None, CultureInfo.InvariantCulture, out var fp))
            {
                fingerprints.Add(fp);
            }
        }

        var (matchJob, response) = _flame.MatchFingerprints(fingerprints);
        await matchJob.StartAsync(cancellationToken);
        if (matchJob.WasSuccessful)
        {
            ParseApiResponse(response.BodyAsString);
        }

        _pendingHashes.Clear();
        await GetProjectsInfo(cancellationToken);
    }

    private void ParseApiResponse(string responseBody)
    {
        // the same fingerprint envelope EnsureMetadataTask consumes
        FlameFingerprintResponseJson? response;
        try
        {
            response = FlameMod.ParseResponse(responseBody, FlameModJsonContext.Default.FlameFingerprintResponseJson);
        }
        catch (JsonParseException)
        {
            return;
        }

        {
            var exactMatches = response.Data?.ExactMatches;
            if (exactMatches is null or { Count: 0 })
            {
                // "No matches found for fingerprint search!"
                return;
            }

            foreach (var match in exactMatches)
            {
                if (match?.File is not { } fileObj)
                {
                    // "Fingerprint match is empty!"
                    return;
                }

                var fingerprint = (fileObj.FileFingerprint ?? 0).ToString(CultureInfo.InvariantCulture);
                if (!_pendingHashes.TryGetValue(fingerprint, out var mod))
                {
                    // "Invalid fingerprint from the API response."
                    continue;
                }

                EmitStatus($"Parsing API response from CurseForge for '{mod.Name}'...");
                if (fileObj.IsAvailable == true)
                {
                    _resolvedFiles[mod.Path] = new ResolvedFile
                    {
                        AddonId = fileObj.ModId ?? 0,
                        Version = fileObj.Id ?? 0,
                        Enabled = mod.Enabled,
                        IsMod = mod.IsMod,
                    };
                }
            }
        }
    }

    private async Task GetProjectsInfo(CancellationToken cancellationToken)
    {
        EmitStatus("Finding project info from CurseForge...");
        SetProgress(3, 5);
        var addonIds = _resolvedFiles.Values
            .Where(resolved => resolved.Slug.Length == 0)
            .Select(resolved => resolved.AddonId.ToString(CultureInfo.InvariantCulture))
            .ToList();

        if (addonIds.Count == 0)
        {
            await BuildZipAsync(cancellationToken);
            return;
        }

        NetJob projJob;
        NetRequest projRequest;
        var single = addonIds.Count == 1;
        if (single)
        {
            (projJob, projRequest) = _flame.GetProject(addonIds[0]);
        }
        else
        {
            (projJob, projRequest) = _flame.GetProjects(addonIds);
        }

        await projJob.StartAsync(cancellationToken);
        if (!projJob.WasSuccessful)
        {
            EmitFailed(projJob.FailReason);
            return;
        }

        // a broken response body only skips the project-info enrichment: the export still
        // builds its zip (the former kind-failure flow; the syntax EmitFailed face is
        // retired as the more reasonable failure — the export completes either way)
        List<FlameProjectJson?>? entries = null;
        try
        {
            entries = single
                ? [FlameMod.ParseResponse(projRequest.BodyAsString,
                       FlameModJsonContext.Default.FlameProjectEnvelopeJson).Data
                   ?? throw new JsonParseException("'data' is not an object")]
                : FlameMod.ParseResponse(projRequest.BodyAsString,
                       FlameModJsonContext.Default.FlameProjectListJson).Data
                  ?? throw new JsonParseException("'data' is not an array");
        }
        catch (JsonParseException) { }

        if (entries is not null)
        {
            foreach (var entryObj in entries)
            {
                try
                {
                    if (entryObj is null)
                    {
                        continue;
                    }

                    EmitStatus($"Parsing API response from CurseForge for '{entryObj.Name ?? ""}'...");

                    var pack = new IndexedPack();
                    FlameMod.LoadIndexedPack(pack, entryObj);
                    foreach (var (key, val) in _resolvedFiles)
                    {
                        if (val.AddonId.ToString(CultureInfo.InvariantCulture) == pack.AddonId)
                        {
                            var authors = string.Join(", ", pack.Authors.Select(a => a.Name));
                            _resolvedFiles[key] = new ResolvedFile
                            {
                                AddonId = val.AddonId,
                                Version = val.Version,
                                Enabled = val.Enabled,
                                IsMod = val.IsMod,
                                Name = pack.Name,
                                Slug = pack.Slug,
                                Authors = authors,
                            };
                        }
                    }
                }
                catch (JsonParseException) { }
            }
        }
        await BuildZipAsync(cancellationToken);
    }

    private async Task BuildZipAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        EmitStatus("Adding files...");
        SetProgress(4, 5);

        try
        {
            using var stream = File.Create(options.Output);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            // the ExportToZipTask layout: everything under the game root, prefixed with
            // "overrides/", minus the files that the manifest references by id
            var exclude = _resolvedFiles.Keys
                .Select(path => Path.GetRelativePath(_gameRoot, path).Replace('\\', '/'))
                .ToHashSet();

            foreach (var file in _files)
            {
                var relative = GetRelativePath(file);
                if (relative.Length == 0 || exclude.Contains(relative))
                {
                    continue;
                }

                if (File.Exists(file))
                {
                    zip.CreateEntryFromFile(file, "overrides/" + relative, CompressionLevel.Optimal);
                }
            }

            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            {
                await writer.WriteAsync(GenerateIndex());
            }

            var modlistEntry = zip.CreateEntry("modlist.html", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(modlistEntry.Open(), Encoding.UTF8))
            {
                await writer.WriteAsync(GenerateHtml());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            EmitFailed("Could not write the export zip: " + e.Message);
            return;
        }

        EmitSucceeded();
    }

    private string GenerateIndex()
    {
        var obj = new JsonObject
        {
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = options.Name,
            ["version"] = options.Version,
            ["author"] = options.Author,
            ["overrides"] = "overrides",
        };

        var version = new JsonObject();

        var profile = options.Instance.GetPackProfile();
        profile.Load(); // (MinecraftInstance loads its profile lazily in the C++; make it explicit here)

        // collect all supported components
        var minecraft = profile.GetComponent("net.minecraft");
        var quilt = profile.GetComponent("org.quiltmc.quilt-loader");
        var fabric = profile.GetComponent("net.fabricmc.fabric-loader");
        var forge = profile.GetComponent("net.minecraftforge");
        var neoforge = profile.GetComponent("net.neoforged");

        // convert all available components to manifest mod loaders
        var id = "";
        if (quilt is not null)
        {
            id = "quilt-" + quilt.Version;
        }
        else if (fabric is not null)
        {
            id = "fabric-" + fabric.Version;
        }
        else if (forge is not null)
        {
            id = "forge-" + forge.Version;
        }
        else if (neoforge is not null)
        {
            id = "neoforge-";
            if (minecraft?.Version == "1.20.1")
            {
                id += "1.20.1-";
            }

            id += neoforge.Version;
        }

        if (minecraft is not null)
        {
            version["version"] = minecraft.Version;
        }

        var modLoaders = new JsonArray();
        if (id.Length != 0)
        {
            var loader = new JsonObject
            {
                ["id"] = id,
                ["primary"] = true,
            };
            modLoaders.Add((JsonNode?)loader);
        }

        version["modLoaders"] = modLoaders;

        if (options.RecommendedRam > 0)
        {
            version["recommendedRam"] = options.RecommendedRam;
        }

        obj["minecraft"] = version;

        var files = new JsonArray();
        foreach (var mod in _resolvedFiles.Values)
        {
            var file = new JsonObject
            {
                ["projectID"] = mod.AddonId,
                ["fileID"] = mod.Version,
                ["required"] = mod.Enabled || !options.OptionalFiles,
            };
            files.Add((JsonNode?)file);
        }

        obj["files"] = files;

        return obj.ToJsonString();
    }

    private string GenerateHtml()
    {
        var content = "";
        foreach (var mod in _resolvedFiles.Values)
        {
            if (mod.IsMod)
            {
                var authors = mod.Authors.Length != 0
                    ? WebUtility.HtmlEncode($" (by {mod.Authors})")
                    : "";
                content += Template
                    .Replace("{name}", WebUtility.HtmlEncode(mod.Name))
                    .Replace("{url}", WebUtility.HtmlEncode(ModIndex.GetMetaUrl(ResourceProvider.Flame,
                        mod.AddonId.ToString(CultureInfo.InvariantCulture))))
                    .Replace("{authors}", authors);
            }
        }

        content = "<ul>" + content + "</ul>";
        return content;
    }

    private string GetRelativePath(string file)
    {
        return Path.GetRelativePath(_gameRoot, file).Replace('\\', '/');
    }

    private static long ParseLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
