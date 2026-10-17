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

public sealed class FileResolvingTask : FlintTask
{
    private readonly FlameManifest _manifest;
    private readonly ModrinthApi _modrinth;
    private readonly FlameApi _flame;

    public FileResolvingTask(FlameManifest toProcess, ModrinthApi? modrinth = null, FlameApi? flame = null)
    {
        _manifest = toProcess;
        _modrinth = modrinth ?? new ModrinthApi();
        _flame = flame ?? new FlameApi();
    }

    public FlameManifest GetResults() => _manifest;

    /// <summary>
    /// APPLICATION->settings()->get("FallbackMRBlockedMods") (upstream default: true): when
    /// set, files without a download URL are looked up on Modrinth by their sha1.
    /// </summary>
    public bool FallbackMrBlockedMods { get; set; } = true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_manifest.Files.Count == 0)
        {
            // no file to resolve so leave it empty and emit success immediately
            EmitSucceeded();
            return;
        }
        EmitStatus("Resolving mod IDs...");
        SetProgress(0, 3);

        var fileIds = _manifest.Files.Values
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
        JsonNode doc;
        JsonArray array;
        try
        {
            doc = JsonUtils.RequireObject(JsonNode.Parse(responseBody)
                ?? throw new JsonParseException("Document: empty response"), "Document");
            array = FlameJson.RequireArray(doc, "data");
        }
        catch (Exception e) when (e is JsonException or JsonParseException)
        {
            // "Non-JSON data returned from the CF API" (covers both the syntax error from
            // JsonNode.Parse and the kind error from RequireObject)
            EmitFailed("Invalid data returned from the API.");
            return;
        }

        {
            var hashes = new List<string>();
            foreach (var file in array)
            {
                try
                {
                    var obj = JsonUtils.RequireObject(file);
                    var version = FlameMod.LoadIndexedPackVersion(obj);
                    var fileid = long.Parse(version.FileId, CultureInfo.InvariantCulture);
                    if (!_manifest.Files.TryGetValue(fileid, out var manifestFile))
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
        if (modrinthJob.WasSuccessful)
        {
            JsonNode? doc = null;
            try
            {
                doc = JsonNode.Parse(modrinthResponse.BodyAsString);
            }
            catch (JsonException)
            {
                // fall through to the Flame projects step, like the C++ parse-error path
            }
            if (doc is not null)
            {
                {
                    if (FallbackMrBlockedMods)
                    {
                        try
                        {
                            var entries = doc;
                            foreach (var outFile in _manifest.Files.Values)
                            {
                                var urlValid = Uri.TryCreate(outFile.Version.DownloadUrl, UriKind.Absolute, out _);
                                if (!urlValid && "sha1" == outFile.Version.HashType
                                    && outFile.Version.Hash.Length != 0
                                    && entries[outFile.Version.Hash] is { } entryNode
                                    && entryNode is not null)
                                {
                                    try
                                    {
                                        var file = ModrinthPackIndex.LoadIndexedPackVersion(entryNode);
                                        outFile.Version.DownloadUrl = file.DownloadUrl;
                                    }
                                    catch (JsonParseException)
                                    {
                                    }
                                }
                            }
                        }
                        catch (JsonException)
                        {
                        }
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
        var addonIds = _manifest.Files.Values
            .Select(f => f.ProjectId.ToString(CultureInfo.InvariantCulture)).ToList();

        var (job, response) = _flame.GetProjects(addonIds);
        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        // (the C++ only logs parse problems in this step and still succeeds)
        JsonNode? doc = null;
        try
        {
            doc = JsonNode.Parse(response.BodyAsString);
        }
        catch (JsonException)
        {
        }

        if (doc is not null)
        {
            {
                try
                {
                    var entries = FlameJson.RequireArray(doc, "data");

                    foreach (var entry in entries)
                    {
                        if (entry is not JsonObject entryObj)
                        {
                            continue;
                        }
                        var id = FlameJson.RequireInteger(entryObj, "id");
                        var file = _manifest.Files.Values.FirstOrDefault(f => f.ProjectId == id);
                        if (file is null)
                        {
                            continue;
                        }

                        EmitStatus($"Parsing API response from CurseForge for '{file.Version.FileName}'...");
                        FlameMod.LoadIndexedPack(file.Pack, entryObj);
                        if (file.Pack.ResourceType == ResourceType.World)
                        {
                            file.TargetFolder = "saves";
                        }
                    }
                }
                catch (JsonParseException)
                {
                }
            }
        }
        EmitSucceeded();
    }
}
