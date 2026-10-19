// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/modrinth/ModrinthPackExportTask.{h,cpp}: export an instance to a
// .mrpack (modrinth.index.json + an overrides/ zip). Mods resolved from local packwiz
// metadata (or from the Modrinth API by sha512) are referenced by URL in the index and
// excluded from the overrides; everything else is packed.
//
// Deviations (headless port):
//  - loaderModList() is not part of the P3b MinecraftInstance port; the task builds its own
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
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthPackExportTask(
    string name,
    string version,
    string summary,
    bool optionalFiles,
    MinecraftInstance instance,
    string output,
    MmcZip.FilterFunction? filter = null,
    ModrinthApi? modrinth = null)
    : FlintTask
{
    private static readonly string[] Prefixes = ["mods/", "coremods/", "resourcepacks/", "texturepacks/", "shaderpacks/"];
    private static readonly string[] FileExtensions = ["jar", "litemod", "zip"];

    private readonly string _gameRoot = instance.GameRoot();

    private readonly List<string> _files = [];
    private readonly Dictionary<string, string> _pendingHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedFile> _resolvedFiles = new(StringComparer.Ordinal);
    private readonly ModrinthApi _modrinth = modrinth ?? new ModrinthApi();

    private sealed record ResolvedFile(string Sha1, string Sha512, string Url, long Size, SideType Side);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Searching for files...");

        if (!CollectFiles())
        {
            return;
        }

        await CollectHashes(cancellationToken);
        await MakeApiRequest(cancellationToken);
    }

    private bool CollectFiles()
    {
        _files.Clear();
        if (!MmcZip.CollectFileListRecursively(instance.GameRoot(), null, _files,
                filter != null ? (Func<FileSystemInfo, bool>)(f => filter(GetRelativePath(f.FullName))) : null))
        {
            EmitFailed("Could not search for files");
            return false;
        }

        return true;
    }

    private async Task CollectHashes(CancellationToken cancellationToken)
    {
        await Task.CompletedTask; // keep the async shape of the C++ callback chain

        EmitStatus("Finding file hashes...");

        // instance->loaderModList()->update()
        var modList = new ModFolderModel(instance.ModsRoot());
        modList.Update();

        foreach (var file in _files)
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
                        && ModrinthApi.MrpackHosts.Contains(parsedUrl.Host))
                    {
                        var sha1 = HashUtils.Hash(data, Algorithm.Sha1);

                        _resolvedFiles[relative] = new ResolvedFile(sha1, sha512, url,
                            new FileInfo(file).Length, metadata.Side);

                        // nice! we've managed to resolve based on local metadata!
                        // no need to enqueue it
                        continue;
                    }
                }
            }

            _pendingHashes[relative] = sha512;
        }
    }

    private async Task MakeApiRequest(CancellationToken cancellationToken)
    {
        if (_pendingHashes.Count == 0)
        {
            BuildZip();
            return;
        }

        EmitStatus("Finding versions for hashes...");
        var (_, request) = _modrinth.CurrentVersions(_pendingHashes.Values.ToList(), "sha512");
        await request.StartAsync(cancellationToken);
        if (!request.WasSuccessful)
        {
            EmitFailed(request.FailReason);
            return;
        }

        ParseApiResponse(request.BodyAsString);
        BuildZip();
    }

    private void ParseApiResponse(string response)
    {
        var entries = ModrinthJson.ParseVersionLookup(response);
        if (entries is null)
        {
            EmitFailed("Failed to parse versions response: empty response");
            return;
        }

        foreach (var (relative, sha512) in _pendingHashes)
        {
            if (!entries.TryGetValue(sha512, out var version) || version.Files is not { } filesArray)
            {
                continue;
            }

            foreach (var file in filesArray)
            {
                if (file.Hashes?.TryGetValue("sha512", out var hash) != true || hash != sha512)
                {
                    continue;
                }

                // map the file to the url
                _resolvedFiles[relative] = new ResolvedFile(
                    Sha1: file.Hashes.GetValueOrDefault("sha1", ""),
                    Sha512: sha512,
                    Url: file.Url ?? "",
                    Size: file.Size ?? 0,
                    Side: SideType.Invalid);
                break;
            }
        }

        _pendingHashes.Clear();
    }

    private void BuildZip()
    {
        EmitStatus("Adding files...");

        try
        {
            using var stream = File.Create(output);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            // the ExportToZipTask layout: everything under the game root, prefixed with
            // "overrides/", minus the files that the index references by URL
            foreach (var file in _files)
            {
                var relative = GetRelativePath(file);
                if (relative.Length == 0 || _resolvedFiles.ContainsKey(relative))
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
            EmitFailed("Could not write the export zip: " + e.Message);
            return;
        }

        EmitSucceeded();
    }

    private string GenerateIndex()
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

        var filesOut = new List<MrPackFileJson>();
        foreach (var (pathIn, value) in _resolvedFiles)
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

            filesOut.Add(new MrPackFileJson(
                Env: new MrPackEnvJson(Client: client, Server: server),
                Path: path,
                Downloads: [value.Url],
                Hashes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sha1"] = value.Sha1,
                    ["sha512"] = value.Sha512,
                },
                FileSize: value.Size));
        }

        var index = new MrPackIndexJson
        {
            FormatVersion = 1,
            Game = "minecraft",
            Name = name,
            VersionId = version,
            Summary = string.IsNullOrEmpty(summary) ? null : summary,
            Dependencies = dependencies,
            Files = filesOut,
        };

        return JsonSerializer.Serialize(index, MrPackJsonContext.Default.MrPackIndexJson);
    }

    private string GetRelativePath(string file)
    {
        return Path.GetRelativePath(_gameRoot, file).Replace('\\', '/');
    }
}
