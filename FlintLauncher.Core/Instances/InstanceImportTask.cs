// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/InstanceImportTask.{h,cpp}: import a modpack zip (local file or URL) by
// detecting its type from marker files and dispatching to the matching creation task:
//   modrinth.index.json      -> Modrinth (ModrinthCreationTask)
//   bin/modpack.jar          -> Technic (deferred, P6-b)
//   bin/version.json
//   manifest.json            -> Flame (FlameCreationTask)
//   */instance.cfg           -> plain MultiMC folder import (NullInstance + rename)
//
// Deviations from the C++:
//  - The platform marker scan runs directly over the zip entries (the ArchiveReader/
//    ExtractZipTask pair becomes MMCZip.ExtractDir).
//  - The icon import (IconUtils/IconList application layer) is not ported; the pack icon
//    detection is skipped and the icon key is passed through unchanged.
//  - Remote downloads use MetaCache when set (APPLICATION->metacache()), otherwise a plain
//    file download into the staging folder.
//  - The Technic branch runs the TechnicPackProcessor (P6-b).

using System.IO.Compression;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Instances;

public sealed partial class InstanceImportTask : InstanceTask
{
    private enum ModpackType : byte
    {
        Unknown,
        MultiMc,
        Technic,
        Flame,
        Modrinth,
    }

    private readonly Uri _sourceUrl;
    private readonly ModrinthApi _modrinth;
    private readonly FlameApi _flame;
    private readonly bool _trustedSource;
    private readonly IReadOnlyDictionary<string, string> _extraInfo;
    private string archivePath = "";
    private ModpackType modpackType = ModpackType.Unknown;

    /// <summary>APPLICATION->metacache() hook; used for the download-from-URL path.</summary>
    public HttpMetaCache? MetaCache { get; set; }

    /// <summary>
    /// The optional-mods selection (fix-2d) handed to the Modrinth/Flame creation tasks
    /// (Modrinth: manifest paths, Flame: relative target paths; see the tasks' property
    /// notes). Unselected/unknown optional files keep the ".disabled" install behavior.
    /// </summary>
    public IReadOnlyDictionary<string, bool> OptionalModsSelection { get; set; } =
        new Dictionary<string, bool>();

    public InstanceImportTask(Uri sourceUrl, bool trustedSource,
                              IReadOnlyDictionary<string, string>? extraInfo = null,
                              ModrinthApi? modrinth = null, FlameApi? flame = null)
    {
        _modrinth = modrinth ?? new ModrinthApi();
        _flame = flame ?? new FlameApi();
        _sourceUrl = sourceUrl;
        _trustedSource = trustedSource;
        _extraInfo = extraInfo ?? new Dictionary<string, string>();
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_sourceUrl.IsFile)
        {
            archivePath = _sourceUrl.LocalPath;
            await ProcessZipPack(cancellationToken);
        }
        else
        {
            EmitStatus("Downloading modpack:\n" + _sourceUrl);
            await DownloadFromUrl(cancellationToken);
        }
    }

    private async Task DownloadFromUrl(CancellationToken cancellationToken)
    {
        if (MetaCache is not null)
        {
            var path = _sourceUrl.Host + '/' + _sourceUrl.AbsolutePath.TrimStart('/');

            var entry = MetaCache.ResolveEntry("general", path);
            entry.SetStale(true);
            archivePath = entry.GetFullPath();

            var filesNetJob = new NetJob("Modpack download");
            filesNetJob.AddNetRequest(NetRequest.MakeCached(MetaCache, _sourceUrl, entry));
            await filesNetJob.StartAsync(cancellationToken);
            if (!filesNetJob.WasSuccessful)
            {
                EmitFailed(filesNetJob.FailReason);
                return;
            }
        }
        else
        {
            // headless fallback: plain download into the staging folder
            archivePath = FileSystem.PathCombine(StagingPath, Path.GetFileName(_sourceUrl.AbsolutePath));
            FileSystem.EnsureFilePathExists(archivePath);
            var request = NetRequest.MakeFile(_sourceUrl, archivePath);
            await request.StartAsync(cancellationToken);
            if (!request.WasSuccessful)
            {
                EmitFailed(request.FailReason);
                return;
            }
        }

        await ProcessZipPack(cancellationToken);
    }

    private async Task ProcessZipPack(CancellationToken cancellationToken)
    {
        EmitStatus("Attempting to determine instance type");
        var extractDir = StagingPath;

        // open the zip and find relevant files in it
        // NOTE: Prioritize modpack platforms that aren't searched for recursively.
        // Especially Flame has a very common filename for its manifest, which may appear
        // inside overrides for example
        // https://docs.modrinth.com/docs/modpacks/format_definition/#storage
        var root = "";
        try
        {
            using var packZip = ZipFile.OpenRead(archivePath);
            foreach (var entry in packZip.Entries)
            {
                var fileName = entry.FullName.Replace('\\', '/');

                if (fileName == "modrinth.index.json")
                {
                    // process as Modrinth pack
                    modpackType = ModpackType.Modrinth;
                    break;
                }
                if (fileName is "bin/modpack.jar" or "bin/version.json")
                {
                    // process as Technic pack
                    Directory.CreateDirectory(FileSystem.PathCombine(extractDir, "minecraft"));
                    extractDir = FileSystem.PathCombine(extractDir, "minecraft");
                    modpackType = ModpackType.Technic;
                    break;
                }
                if (fileName == "manifest.json")
                {
                    modpackType = ModpackType.Flame;
                    break;
                }
                if (Path.GetFileName(fileName) == "instance.cfg")
                {
                    modpackType = ModpackType.MultiMc;
                    root = CleanPath(Path.GetDirectoryName(fileName)?.Replace('\\', '/') ?? "");
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            EmitFailed("Unable to open supplied modpack zip file.");
            return;
        }

        if (modpackType == ModpackType.Unknown)
        {
            EmitFailed("Archive does not contain a recognized modpack type.");
            return;
        }
        EmitStatus("Extracting modpack");

        // make sure we extract just the pack
        var extracted = MmcZip.ExtractDir(archivePath, root, extractDir);
        if (extracted is null)
        {
            EmitFailed("Failed to extract the modpack zip.");
            return;
        }

        await ExtractFinished(cancellationToken);
    }

    private async Task ExtractFinished(CancellationToken cancellationToken)
    {
        switch (modpackType)
        {
            case ModpackType.MultiMc:
                ProcessMultiMc();
                return;
            case ModpackType.Technic:
                await ProcessTechnic(cancellationToken);
                return;
            case ModpackType.Flame:
                await ProcessFlame(cancellationToken);
                return;
            case ModpackType.Modrinth:
                await ProcessModrinth(cancellationToken);
                return;
            case ModpackType.Unknown:
                EmitFailed("Archive does not contain a recognized modpack type.");
                return;
        }
    }

    private async Task ProcessFlame(CancellationToken cancellationToken)
    {
        string packId = "";
        string packVersionId = "";
        if (_extraInfo.Count != 0)
        {
            packId = _extraInfo.TryGetValue("pack_id", out var id) ? id : "";
            packVersionId = _extraInfo.TryGetValue("pack_version_id", out var versionId) ? versionId : "";
        }

        var instCreationTask = new FlameCreationTask(StagingPath, _trustedSource,
            Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution."),
            packId, packVersionId, _modrinth, flame: _flame)
        {
            OptionalModsSelection = OptionalModsSelection,
        };

        instCreationTask.SetName(ModifiedName());
        instCreationTask.SetOriginalName(OriginalName(), Version());
        // (the icon import from the pack is not ported — see the header note)
        instCreationTask.SetIcon(Icon);
        instCreationTask.SetGroup(Group);
        instCreationTask.SetConfirmUpdate(ShouldConfirmUpdate());

        await instCreationTask.StartAsync(cancellationToken);

        if (instCreationTask.WasSuccessful)
        {
            SetOverride(instCreationTask.ShouldOverride, instCreationTask.OriginalInstanceId);
            EmitSucceeded();
        }
        else
        {
            EmitFailed(instCreationTask.FailReason);
        }
    }

    private async Task ProcessTechnic(CancellationToken cancellationToken)
    {
        // Port of InstanceImportTask::processTechnic: the marker-detected Technic zip has no
        // separate minecraft version (that only the Solder/zip install tasks know), so the
        // pack processor runs with an empty version — a modpack.jar without version.json
        // therefore fails like upstream.
        var failure = await TechnicPackProcessor.RunAsync(
            Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution."),
            Name(), Icon, StagingPath, cancellationToken: cancellationToken);
        if (failure is not null)
        {
            EmitFailed(failure);
            return;
        }

        EmitSucceeded();
    }

    private void ProcessMultiMc()
    {
        // D4: the staged MultiMC import writes its fixups (fresh uuid, name, playtime reset)
        // through the typed adapter — the committed folder is Flint format (instance.json);
        // the cfg inside the zip, if any, stays untouched
        var instance = new NullInstance(
            new InstanceJsonSettingsObject(StagingPath, Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution.")),
            StagingPath);

        // reset time played on import... because packs.
        instance.ResetTimePlayed();

        // UUID is carried over on export, but this is a distinct instance, so give it its own
        instance.RegenerateUuid();

        // set a new nice name
        instance.SetName(Name());

        // if the icon was specified by user, use that. otherwise keep the pack's icon key
        // (the icon file import itself needs the icon list layer and is not ported)
        if (Icon != "default")
        {
            instance.SetIconKey(Icon);
        }

        EmitSucceeded();
    }

    private async Task ProcessModrinth(CancellationToken cancellationToken)
    {
        string packId = "";
        string packVersionId = "";
        if (_extraInfo.Count != 0)
        {
            packId = _extraInfo.TryGetValue("pack_id", out var id) ? id : "";
            packVersionId = _extraInfo.TryGetValue("pack_version_id", out var versionId) ? versionId : "";
        }
        else
        {
            // FIXME: Find a way to get the ID in directly imported ZIPs
            var match = SourceUrlPackIdRegex().Match(_sourceUrl.ToString());
            if (match.Success)
            {
                packId = match.Groups[1].Value;
            }
        }

        var instCreationTask = new ModrinthCreationTask(StagingPath, _trustedSource,
            Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution."),
            packId, packVersionId, modrinth: _modrinth)
        {
            OptionalModsSelection = OptionalModsSelection,
        };

        instCreationTask.SetName(ModifiedName());
        instCreationTask.SetOriginalName(OriginalName(), Version());
        // (the icon import from the pack is not ported — see the header note)
        instCreationTask.SetIcon(Icon);
        instCreationTask.SetGroup(Group);
        instCreationTask.SetConfirmUpdate(ShouldConfirmUpdate());

        await instCreationTask.StartAsync(cancellationToken);

        if (instCreationTask.WasSuccessful)
        {
            SetOverride(instCreationTask.ShouldOverride, instCreationTask.OriginalInstanceId);
            EmitSucceeded();
        }
        else
        {
            EmitFailed(instCreationTask.FailReason);
        }
    }

    /// <summary>Port of the cleanPath helper ('.' and './' prefixes are removed).</summary>
    private static string CleanPath(string path)
    {
        if (path == ".")
        {
            return "";
        }
        var result = path;
        if (result.StartsWith("./", StringComparison.Ordinal))
        {
            result = result[2..];
        }
        return result;
    }

    [GeneratedRegex(@"data\/([^\/]*)\/versions")]
    private static partial Regex SourceUrlPackIdRegex();
}
