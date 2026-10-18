// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/flame/FileResolvingTask.{h,cpp} (+ the BlockedMod data struct of
// ui/dialogs/BlockedModsDialog.h): resolve the manifest's project/file ids through the
// CurseForge API, recover blocked downloads from Modrinth by sha1 when possible, and load
// the per-file project metadata (pack, resource type -> target folder).
//
// Blocked-mods semantics (kept from upstream): a file whose resolved downloadUrl is empty
// (the CurseForge API only hands out direct download URLs to bundled API keys) resolves
// fine here — it simply has no download URL. The creation task surfaces those files as
// BlockedMod entries (name/websiteUrl/hash/targetFolder) for the BlockedModsDialog data
// flow; the optional BlockedModLocalFileResolver hook stands in for the dialog's
// match-a-local-file step (see FlameCreationTask).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>Port of the BlockedMod struct (BlockedModsDialog.h): the per-file data the UI
/// dialog consumes to let users match locally downloaded copies.</summary>
public sealed class BlockedMod
{
    public string Name { get; set; } = "";
    public string WebsiteUrl { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Matched { get; set; }
    public string LocalPath { get; set; } = "";
    public string TargetFolder { get; set; } = "";
    public bool Disabled { get; set; }
    public bool Move { get; set; }
}

public sealed class FileResolvingTask(FlameManifest toProcess, ModrinthApi? modrinth = null, FlameApi? flame = null) : FlintTask
{
    private readonly ModrinthApi _modrinth = modrinth ?? new ModrinthApi();
    private readonly FlameApi _flame = flame ?? new FlameApi();

    public FlameManifest GetResults() => toProcess;

    /// <summary>
    /// APPLICATION->settings()->get("FallbackMRBlockedMods") (upstream default: true): when
    /// set, files without a download URL are looked up on Modrinth by their sha1.
    /// </summary>
    public bool FallbackMrBlockedMods { get; set; } = true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (toProcess.Files.Count == 0)
        {
            // no file to resolve so leave it empty and emit success immediately
            EmitSucceeded();
            return;
        }

        EmitStatus("Resolving mod IDs...");
        SetProgress(0, 3);

        var fileIds = toProcess.Files.Values
            .Select(f => f.FileId.ToString(CultureInfo.InvariantCulture)).ToList();
        var (job, response) = _flame.GetFiles(fileIds);
        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        await NetJobFinished(response.BodyAsString, cancellationToken);
    }

    private async Task NetJobFinished(string responseBody, CancellationToken cancellationToken)
    {
        SetProgress(1, 3);
        // job to check modrinth for blocked projects
        FlameFileListJson doc;
        try
        {
            doc = FlameMod.ParseResponse(responseBody, FlameModJsonContext.Default.FlameFileListJson);
        }
        catch (JsonParseException)
        {
            // "Non-JSON data returned from the CF API" (syntax and kind errors alike)
            EmitFailed("Invalid data returned from the API.");
            return;
        }

        {
            var hashes = new List<string>();
            foreach (var fileDto in doc.Data ?? [])
            {
                try
                {
                    var version = FlameMod.LoadIndexedPackVersion(fileDto
                                      ?? throw new JsonParseException("Value is not an object"));
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
                catch (JsonParseException)
                {
                    // "Non-JSON data returned from the CF API"
                    EmitFailed("Invalid data returned from the API.");
                    return;
                }
                catch (FormatException)
                {
                    EmitFailed("Invalid data returned from the API.");
                    return;
                }
            }

            if (hashes.Count == 0)
            {
                await GetFlameProjects(cancellationToken);
                return;
            }

            await ModrinthFallback(hashes, cancellationToken);
        }
    }

    /// <summary>The Modrinth fallback for blocked files (currentVersions by sha1).</summary>
    private async Task ModrinthFallback(List<string> hashes, CancellationToken cancellationToken)
    {
        var (modrinthJob, modrinthResponse) = _modrinth.CurrentVersions(hashes, "sha1");
        await modrinthJob.StartAsync(cancellationToken);
        if (modrinthJob.WasSuccessful && FallbackMrBlockedMods)
        {
            // a non-object (or invalid) body parses to no entries: every lookup just misses,
            // like the C++ parse-error path
            var entries = ModrinthJson.ParseVersionLookup(modrinthResponse.BodyAsString);
            if (entries is not null)
            {
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
                        catch (JsonParseException) { }
                    }
                }
            }
        }

        // the C++ moves on to the projects step even when the Modrinth task failed
        await GetFlameProjects(cancellationToken);
    }

    private async Task GetFlameProjects(CancellationToken cancellationToken)
    {
        SetProgress(2, 3);
        var addonIds = toProcess.Files.Values
            .Select(f => f.ProjectId.ToString(CultureInfo.InvariantCulture)).ToList();

        var (job, response) = _flame.GetProjects(addonIds);
        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        // (the C++ only logs parse problems in this step and still succeeds)
        FlameProjectListJson? doc = null;
        try
        {
            doc = FlameMod.ParseResponse(response.BodyAsString,
                FlameModJsonContext.Default.FlameProjectListJson);
        }
        catch (JsonParseException) { }

        if (doc is not null)
        {
            try
            {
                foreach (var entry in doc.Data ?? [])
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

                    EmitStatus($"Parsing API response from CurseForge for '{file.Version.FileName}'...");
                    FlameMod.LoadIndexedPack(file.Pack, projectDto);
                    if (file.Pack.ResourceType == ResourceType.World)
                    {
                        file.TargetFolder = "saves";
                    }
                }
            }
            catch (JsonParseException) { }
        }

        EmitSucceeded();
    }
}
