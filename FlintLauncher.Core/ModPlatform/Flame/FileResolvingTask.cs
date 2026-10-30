// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2024 Trial97 <alexandru.tripon97@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022 Rachel Powers <508861+Ryex@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 kumquat-ir <66188216+kumquat-ir@users.noreply.github.com>
//
// Port of modplatform/flame/FileResolvingTask.{h,cpp} (+ the BlockedMod data struct of
// ui/dialogs/BlockedModsDialog.h): resolve the manifest's project/file ids through the
// CurseForge API, recover blocked downloads from Modrinth by sha1 when possible, and load
// the per-file project metadata (pack, resource type -> target folder).
//
// Blocked-mods semantics (kept from upstream): a file whose resolved downloadUrl is empty
// (the CurseForge API only hands out direct download URLs to bundled API keys) resolves
// fine here — it simply has no download URL. The creation task surfaces those files as
// BlockedMod entries (name/websiteUrl/targetFolder) for the BlockedModsDialog data
// flow; the optional BlockedModLocalFileResolver hook stands in for the dialog's
// match-a-local-file step (see FlameCreationTask). The resolver matches by filename:
// the headless port does not carry the upstream hash column.

using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>Port of the BlockedMod struct (BlockedModsDialog.h): the per-file data the UI
/// dialog consumes to let users match locally downloaded copies. The headless port drops the
/// display-only hash and the dialog's move/copy checkbox (always copies).</summary>
public sealed class BlockedMod
{
    public string Name { get; init; } = "";
    public string WebsiteUrl { get; init; } = "";
    public bool Matched { get; set; }
    public string LocalPath { get; set; } = "";
    public string TargetFolder { get; init; } = "";
    public bool Disabled { get; set; }
}

public static class FileResolvingTask
{
    /// <summary>Resolves the manifest's file/project ids and loads the per-file pack metadata
    /// into <paramref name="toProcess"/> in place. Returning is success; any other exception is
    /// a failure whose message is the reason.
    /// <para><paramref name="fallbackMrBlockedMods"/> is the "FallbackMRBlockedMods" setting
    /// (upstream default: true): when set, files without a download URL are looked up on
    /// Modrinth by their sha1.</para></summary>
    public static async Task RunAsync(FlameManifest toProcess, ModrinthClient modrinth, HttpClient http,
        FlameClient? flame = null, bool fallbackMrBlockedMods = true,
        CancellationToken cancellationToken = default)
    {
        var flameClient = flame ?? new FlameClient();

        if (toProcess.Files.Count == 0)
        {
            // no file to resolve, so leave it empty
            return;
        }

        var fileIds = toProcess.Files.Values
            .Select(f => f.FileId.ToString(CultureInfo.InvariantCulture)).ToList();
        FlameFileList doc;
        try
        {
            doc = await flameClient.Files.ByIds(fileIds).SendTo(http, cancellationToken);
        }
        catch (FlameApiException e)
        {
            throw new InvalidOperationException(e.Message, e);
        }

        await NetJobFinished(doc);

        async Task NetJobFinished(FlameFileList fileList)
        {
            // job to check modrinth for blocked projects
            if (fileList.Data is null)
            {
                // the former RequireArray throw: an absent/null data member is a broken reply,
                // not an empty resolution
                throw new InvalidOperationException("Invalid data returned from the API.");
            }

            var hashes = new List<string>();
            foreach (var fileDto in fileList.Data)
            {
                try
                {
                    var version = FlameMod.LoadIndexedPackVersion(fileDto
                                                                  ?? throw new JsonException("Value is not an object"));
                    var fileid = long.Parse(version.FileId, CultureInfo.InvariantCulture);
                    if (!toProcess.Files.TryGetValue(fileid, out var manifestFile))
                    {
                        continue; // upstream asserts this cannot happen
                    }

                    manifestFile.Version = version;
                    var urlValid = Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out _);
                    if (!urlValid && "sha1" == version.HashType && version.Hash.Length != 0)
                    {
                        hashes.Add(version.Hash);
                    }
                }
                catch (Exception e) when (e is JsonException or FormatException)
                {
                    // "Non-JSON data returned from the CF API"
                    throw new InvalidOperationException("Invalid data returned from the API.", e);
                }
            }

            if (hashes.Count == 0)
            {
                await GetFlameProjects();
                return;
            }

            await ModrinthFallback(hashes);
        }

        // the Modrinth fallback for blocked files (currentVersions by sha1)
        async Task ModrinthFallback(List<string> hashes)
        {
            try
            {
                // a non-object (or invalid) body fails the decode: every lookup just misses,
                // treated the same as the C++ parse-error path
                if (fallbackMrBlockedMods)
                {
                    var entries = await modrinth.Versions.ByFileHashes(hashes, "sha1")
                        .SendTo(http, cancellationToken);
                    foreach (var outFile in toProcess.Files.Values)
                    {
                        var urlValid = Uri.TryCreate(outFile.Version.DownloadUrl, UriKind.Absolute, out _);
                        if (!urlValid && "sha1" == outFile.Version.HashType
                                      && outFile.Version.Hash.Length != 0
                                      && entries.TryGetValue(outFile.Version.Hash, out var entry))
                        {
                            try
                            {
                                var file = ModrinthPackIndex.LoadIndexedPackVersion(entry);
                                outFile.Version.DownloadUrl = file.DownloadUrl;
                            }
                            catch (JsonException) { }
                        }
                    }
                }
            }
            catch (Exception e) when (e is ModrinthApiException)
            {
                // the fallback is best-effort: the sha1-lookup failing must not fail the resolution
            }

            // the C++ moves on to the projects step even when the Modrinth task failed
            await GetFlameProjects();
        }

        async Task GetFlameProjects()
        {
            var addonIds = toProcess.Files.Values
                .Select(f => f.ProjectId.ToString(CultureInfo.InvariantCulture)).ToList();

            FlameProjectList projectList;
            try
            {
                projectList = await flameClient.Projects.ByIds(addonIds).SendTo(http, cancellationToken);
            }
            catch (FlameApiException e)
            {
                throw new InvalidOperationException(e.Message, e);
            }

            // (the C++ only logs parse problems in this step and still succeeds)
            try
            {
                foreach (var entry in projectList.Data ?? [])
                {
                    if (entry is not { } projectDto)
                    {
                        continue;
                    }

                    var id = projectDto.Id ?? 0;
                    var file = toProcess.Files.Values.FirstOrDefault(f => f.ProjectId == id);
                    if (file is null)
                    {
                        continue;
                    }

                    FlameMod.LoadIndexedPack(file.Pack, projectDto);
                    if (file.Pack.ResourceType == ResourceType.World)
                    {
                        file.TargetFolder = "saves";
                    }
                }
            }
            catch (JsonException) { }
        }
    }
}
