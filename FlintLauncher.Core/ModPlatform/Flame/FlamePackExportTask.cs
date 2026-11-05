// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
//
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
//  - loaderModList() is not part of the ported MinecraftInstance; the task builds its own
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
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.Tasks;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using TruePath;

namespace FlintLauncher.Core.ModPlatform.Flame;

using ResourceFolderType = Minecraft.Resources.ResourceType;

public static class FlamePackExportTask
{
    private const string Template = "<li><a href=\"{url}\">{name}{authors}</a></li>\n";
    private static readonly string[] FileExtensions = ["jar", "zip"];

    /// <summary>Port of FlamePackExportOptions (the filter/recommendedRam knobs had no
    /// wired producer and are not ported).</summary>
    public sealed class Options
    {
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required string Author { get; init; }
        public bool OptionalFiles { get; init; }
        public required MinecraftInstance Instance { get; init; }
        public required AbsolutePath? Output { get; init; }
        public required FlameClient Flame { get; init; }
        public required HttpClient Http { get; init; }
    }

    private sealed class ResolvedFile
    {
        public long AddonId { get; init; }
        public long Version { get; init; }
        public bool Enabled { get; init; }
        public bool IsMod { get; init; }
        public string Name { get; init; } = "";
        public string Slug { get; init; } = "";
        public string Authors { get; init; } = "";
    }

    private sealed class HashInfo
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public bool Enabled { get; init; }
        public bool IsMod { get; init; }
    }

    /// <summary>Writes the export zip. Returning is success; any other exception is a failure
    /// whose message is the reason. <paramref name="progress"/> null runs without reporting.</summary>
    public static async Task ExportAsync(Options options, IProgress<TaskProgress>? progress,
        CancellationToken cancellationToken)
    {
        var gameRoot = options.Instance.GameRoot;
        var flame = options.Flame;
        var http = options.Http;

        var files = new List<string>();
        var pendingHashes = new Dictionary<string, HashInfo>(StringComparer.Ordinal);
        var resolvedFiles = new Dictionary<string, ResolvedFile>(StringComparer.Ordinal);

        progress?.Report(new TaskProgress(0, 5));

        CollectFiles();

        await CollectHashes();
        await MakeApiRequest();

        void CollectFiles()
        {
            files.Clear();
            if (!MmcZip.CollectFileListRecursively(options.Instance.GameRoot, null, files))
            {
                throw new InvalidOperationException("Could not search for files");
            }
        }

        async Task CollectHashes()
        {
            progress?.Report(new TaskProgress(1, 5));

            // instance->loaderModList()->update()
            var modList = new ModFolderModel(MaterializedDirectory.Ensure(options.Instance.ModsRoot)!);
            modList.Update();
            var allMods = modList.Mods;

            foreach (var file in files)
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
                    pendingHashes[hash] = new HashInfo
                    {
                        Name = relative,
                        Path = file,
                        Enabled = relative.EndsWith(".zip", StringComparison.Ordinal),
                        IsMod = false,
                    };
                    continue;
                }

                var mod = allMods.FirstOrDefault(m => string.Equals(m.Fileinfo.FullName, file, StringComparison.Ordinal));
                if (mod is not null)
                {
                    if (mod.Type == ResourceFolderType.Folder)
                    {
                        continue;
                    }

                    var metadata = modList.GetMetadata(mod);
                    if (metadata is not null && metadata.Provider == ResourceProvider.Flame)
                    {
                        resolvedFiles[file] = new ResolvedFile
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
                    pendingHashes[modHash] = new HashInfo
                    {
                        Name = mod.Name,
                        Path = file,
                        Enabled = mod.Enabled,
                        IsMod = true,
                    };
                }
            }
        }

        async Task MakeApiRequest()
        {
            if (pendingHashes.Count == 0)
            {
                await BuildZipAsync();
                return;
            }

            progress?.Report(new TaskProgress(2, 5));

            var fingerprints = new List<uint>();
            foreach (var murmur in pendingHashes.Keys)
            {
                if (uint.TryParse(murmur, NumberStyles.None, CultureInfo.InvariantCulture, out var fp))
                {
                    fingerprints.Add(fp);
                }
            }

            try
            {
                ParseApiResponse(await flame.Fingerprints.Match(fingerprints).SendTo(http, cancellationToken));
            }
            catch (FlameApiException)
            {
                // (the C++ only logged the fingerprint failure and continued to the info pass)
            }

            pendingHashes.Clear();
            await GetProjectsInfo();
        }

        void ParseApiResponse(FlameFingerprintResponse response)
        {
            var exactMatches = response.Data?.ExactMatches;
            if (exactMatches is null or { Count: 0 })
            {
                // "No matches found for fingerprint search!"
                return;
            }

            foreach (var match in exactMatches)
            {
                if (match.File is not { } fileObj)
                {
                    // "Fingerprint match is empty!"
                    return;
                }

                var fingerprint = (fileObj.FileFingerprint ?? 0).ToString(CultureInfo.InvariantCulture);
                if (!pendingHashes.TryGetValue(fingerprint, out var mod))
                {
                    // "Invalid fingerprint from the API response."
                    continue;
                }

                if (fileObj.IsAvailable == true)
                {
                    resolvedFiles[mod.Path] = new ResolvedFile
                    {
                        AddonId = fileObj.ModId ?? 0,
                        Version = fileObj.Id ?? 0,
                        Enabled = mod.Enabled,
                        IsMod = mod.IsMod,
                    };
                }
            }
        }

        async Task GetProjectsInfo()
        {
            progress?.Report(new TaskProgress(3, 5));
            var addonIds = resolvedFiles.Values
                .Where(resolved => resolved.Slug.Length == 0)
                .Select(resolved => resolved.AddonId.ToString(CultureInfo.InvariantCulture))
                .ToList();

            if (addonIds.Count == 0)
            {
                await BuildZipAsync();
                return;
            }

            var single = addonIds.Count == 1;
            List<FlameProject?>? entries = null;
            try
            {
                entries = single
                    ?
                    [
                        (
                            await flame.Projects.ById(addonIds[0]).SendTo(http, cancellationToken)).Data
                        ?? throw new JsonException("'data' is not an object"),
                    ]
                    : (await flame.Projects.ByIds(addonIds).SendTo(http, cancellationToken)).Data
                      ?? throw new JsonException("'data' is not an array");
            }
            catch (FlameApiException e) when (e.InnerException is HttpRequestException)
            {
                throw new InvalidOperationException(e.Message);
            }
            catch (Exception e) when (e is FlameApiException or JsonException)
            {
                // a broken response body (the wrapped decode) only skips the project-info
                // enrichment: the export still builds its zip, like the former kind-failure flow
            }

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

                        var pack = new IndexedPack();
                        FlameMod.LoadIndexedPack(pack, entryObj);
                        foreach (var (key, val) in resolvedFiles)
                        {
                            if (val.AddonId.ToString(CultureInfo.InvariantCulture) == pack.AddonId)
                            {
                                var authors = string.Join(", ", pack.Authors.Select(a => a.Name));
                                resolvedFiles[key] = new ResolvedFile
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
                    catch (JsonException) { }
                }
            }
            await BuildZipAsync();
        }

        async Task BuildZipAsync()
        {
            progress?.Report(new TaskProgress(4, 5));

            var outputPath = options.Output?.Value;
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new InvalidOperationException("No output path given for the export zip.");
            }

            try
            {
                using var stream = File.Create(outputPath);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

                // the ExportToZipTask layout: everything under the game root, prefixed with
                // "overrides/", minus the files that the manifest references by id
                var exclude = resolvedFiles.Keys
                    .Select(path => Path.GetRelativePath(gameRoot.Value, path).Replace('\\', '/'))
                    .ToHashSet();

                foreach (var file in files)
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
                throw new InvalidOperationException("Could not write the export zip: " + e.Message);
            }
        }

        string GenerateIndex()
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

            obj["minecraft"] = version;

            var manifestFiles = new JsonArray();
            foreach (var mod in resolvedFiles.Values)
            {
                var file = new JsonObject
                {
                    ["projectID"] = mod.AddonId,
                    ["fileID"] = mod.Version,
                    ["required"] = mod.Enabled || !options.OptionalFiles,
                };
                manifestFiles.Add((JsonNode?)file);
            }

            obj["files"] = manifestFiles;

            return obj.ToJsonString();
        }

        string GenerateHtml()
        {
            var content = "";
            foreach (var mod in resolvedFiles.Values)
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

        string GetRelativePath(string file) =>
            Path.GetRelativePath(gameRoot.Value, file).Replace('\\', '/');

        static long ParseLong(string value) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }
}
