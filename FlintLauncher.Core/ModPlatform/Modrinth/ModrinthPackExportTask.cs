// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
//
// Port of modplatform/modrinth/ModrinthPackExportTask.{h,cpp}: export an instance to a
// .mrpack (modrinth.index.json + an overrides/ zip). Mods resolved from local packwiz
// metadata (or from the Modrinth API by sha512) are referenced by URL in the index and
// excluded from the overrides; everything else is packed.
//
// Deviations (headless port):
//  - loaderModList() is not part of the ported MinecraftInstance; the task builds its own
//    ModFolderModel over instance.ModsRoot() (which is what the C++ list reads) and uses
//    ModFolderModel.GetMetadata for the local-metadata resolution.
//  - The ExportToZipTask becomes direct ZipArchive writing with the same layout
//    ("overrides/" prefix, modrinth.index.json as an extra entry, resolved files excluded).
//  - The progress/step plumbing of the Qt signals is reduced to status/progress updates.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Modrinth;
using TruePath;
using TruePath.SystemIo;
using MrPackJsonContext = FlintLauncher.ExternalApi.Modrinth.MrPackJsonContext;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public static class ModrinthPackExportTask
{
    private static readonly string[] Prefixes = ["mods/", "coremods/", "resourcepacks/", "texturepacks/", "shaderpacks/"];
    private static readonly string[] FileExtensions = ["jar", "litemod", "zip"];

    private sealed record ResolvedFile(string Sha1, string Sha512, string Url, long Size, SideType Side);

    /// <summary>Writes the mrpack. Returning is success; any other exception is a failure
    /// whose message is the reason.</summary>
    public static async Task ExportAsync(
        string name,
        string version,
        string summary,
        bool optionalFiles,
        MinecraftInstance instance,
        AbsolutePath output,
        ModrinthClient modrinth,
        HttpClient http,
        CancellationToken cancellationToken,
        MmcZip.FilterFunction? filter = null)
    {
        var gameRoot = instance.GameRoot;

        var files = new List<string>();
        var pendingHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolvedFiles = new Dictionary<string, ResolvedFile>(StringComparer.Ordinal);

        CollectFiles();
        await CollectHashes();
        await MakeApiRequest();

        void CollectFiles()
        {
            files.Clear();
            if (!MmcZip.CollectFileListRecursively(instance.GameRoot, null, files,
                    filter != null ? (Func<FileSystemInfo, bool>)(f => filter(GetRelativePath(f.FullName))) : null))
            {
                throw new InvalidOperationException("Could not search for files");
            }
        }

        async Task CollectHashes()
        {
            // instance->loaderModList()->update()
            var modList = new ModFolderModel(MaterializedDirectory.Wrap(instance.ModsRoot)!);
            modList.Update();

            foreach (var file in files)
            {
                var relative = GetRelativePath(file);
                // require sensible file types
                if (!Prefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!FileExtensions.Any(extension =>
                        relative.EndsWith('.' + extension, StringComparison.Ordinal)
                        || relative.EndsWith('.' + extension + ".disabled", StringComparison.Ordinal)))
                {
                    continue;
                }

                byte[] data;
                try
                {
                    data = await File.ReadAllBytesAsync(file, cancellationToken);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // "Could not open {file} for hashing"
                    continue;
                }

                var sha512 = HashUtils.Hash(data, Algorithm.Sha512);

                var mod = modList.AllMods().Find(m => string.Equals(m.Fileinfo.FullName, file, StringComparison.Ordinal));
                if (mod is not null)
                {
                    var metadata = modList.GetMetadata(mod);
                    if (metadata is not null)
                    {
                        var url = metadata.Url;
                        // ensure the url is permitted on modrinth.com
                        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl)
                            && ModrinthClient.MrpackHosts.Contains(parsedUrl.Host))
                        {
                            var sha1 = HashUtils.Hash(data, Algorithm.Sha1);

                            resolvedFiles[relative] = new ResolvedFile(sha1, sha512, url,
                                new FileInfo(file).Length, metadata.Side);

                            // nice! we've managed to resolve based on local metadata!
                            // no need to enqueue it
                            continue;
                        }
                    }
                }

                pendingHashes[relative] = sha512;
            }
        }

        async Task MakeApiRequest()
        {
            if (pendingHashes.Count == 0)
            {
                BuildZip();
                return;
            }

            var entries = await modrinth.Versions
                .ByFileHashes(pendingHashes.Values.ToList(), "sha512").SendTo(http, cancellationToken);

            ParseApiResponse(entries);
            BuildZip();
        }

        void ParseApiResponse(Dictionary<string, ModrinthVersion> entries)
        {
            foreach (var (relative, sha512) in pendingHashes)
            {
                if (!entries.TryGetValue(sha512, out var v))
                {
                    continue;
                }

                foreach (var file in v.Files)
                {
                    if (file.Hashes?.TryGetValue("sha512", out var hash) != true || hash != sha512)
                    {
                        continue;
                    }

                    // map the file to the url
                    resolvedFiles[relative] = new ResolvedFile(
                        Sha1: file.Hashes.GetValueOrDefault("sha1", ""),
                        Sha512: sha512,
                        Url: file.Url,
                        Size: file.Size,
                        Side: SideType.Invalid);
                    break;
                }
            }

            pendingHashes.Clear();
        }

        void BuildZip()
        {
            try
            {
                using var stream = PathIo.Create(output);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

                // the ExportToZipTask layout: everything under the game root, prefixed with
                // "overrides/", minus the files that the index references by URL
                foreach (var file in files)
                {
                    var relative = GetRelativePath(file);
                    if (relative.Length == 0 || resolvedFiles.ContainsKey(relative))
                    {
                        continue;
                    }

                    if (File.Exists(file))
                    {
                        zip.CreateEntryFromFile(file, "overrides/" + relative, CompressionLevel.Optimal);
                    }
                }

                var indexEntry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
                using var writer = new StreamWriter(indexEntry.Open(), Encoding.UTF8);
                writer.Write(GenerateIndex());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
            {
                throw new InvalidOperationException("Could not write the export zip: " + e.Message);
            }
        }

        string GenerateIndex()
        {
            // collect all supported components
            var profile = instance.GetPackProfile();
            profile.Load(); // (MinecraftInstance loads its profile lazily in the C++; make it explicit here)
            Component? minecraft = profile.GetComponent("net.minecraft");
            Component? quilt = profile.GetComponent("org.quiltmc.quilt-loader");
            Component? fabric = profile.GetComponent("net.fabricmc.fabric-loader");
            Component? forge = profile.GetComponent("net.minecraftforge");
            Component? neoForge = profile.GetComponent("net.neoforged");

            // convert all available components to mrpack dependencies (the C++ reads the raw
            // m_version of each component)
            var dependencies = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (minecraft is not null)
            {
                dependencies["minecraft"] = minecraft.Version;
            }

            if (quilt is not null)
            {
                dependencies["quilt-loader"] = quilt.Version;
            }

            if (fabric is not null)
            {
                dependencies["fabric-loader"] = fabric.Version;
            }

            if (forge is not null)
            {
                dependencies["forge"] = forge.Version;
            }

            if (neoForge is not null)
            {
                dependencies["neoforge"] = neoForge.Version;
            }

            var filesOut = new List<MrPackFile>();
            foreach (var (pathIn, value) in resolvedFiles)
            {
                var path = pathIn;
                var client = "required";
                var server = "required";

                // detect disabled mod
                var suffix = Path.GetExtension(path).TrimStart('.');
                if (optionalFiles && suffix == "disabled")
                {
                    // rename it
                    path = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path))
                        .Replace('\\', '/');
                    client = "optional";
                    server = "optional";
                }

                // a server side mod does not imply that the mod does not work on the client
                // however, if a mrpack mod is marked as server-only it will not install on the client
                if (value.Side.Value == SideTypeValue.ClientSide)
                {
                    server = "unsupported";
                }

                filesOut.Add(new MrPackFile(
                    Env: new MrPackEnv(Client: client, Server: server),
                    Path: path,
                    Downloads: [value.Url],
                    Hashes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["sha1"] = value.Sha1,
                        ["sha512"] = value.Sha512,
                    },
                    FileSize: value.Size));
            }

            var index = new MrPackIndex
            {
                FormatVersion = 1,
                Game = "minecraft",
                Name = name,
                VersionId = version,
                Summary = string.IsNullOrEmpty(summary) ? null : summary,
                Dependencies = dependencies,
                Files = filesOut,
            };

            return JsonSerializer.Serialize(index, MrPackJsonContext.Default.MrPackIndex);
        }

        string GetRelativePath(string file) =>
            Path.GetRelativePath(gameRoot.Value, file).Replace('\\', '/');
    }
}
