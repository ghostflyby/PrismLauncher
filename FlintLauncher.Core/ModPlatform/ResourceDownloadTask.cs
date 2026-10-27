// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2023 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// Port of launcher/ResourceDownloadTask.{h,cpp}: download an IndexedVersion's primary file
// into the target folder model's directory and keep the per-mod metadata (.pw.toml) in
// sync. A SequentialTask with (optionally, when isIndexed) the LocalResourceUpdateTask
// first and the file download second; the old file is only uninstalled after the new
// download succeeded (the hasOldResource indirection).
//
// Deviations:
//  - The C++ reads the Modrinth download-meta (reason/game_version/loader/dependent_on)
//    from m_pack_model->instance(); this port's ResourceFolderModel carries no instance
//    back-pointer, so the instance is an optional constructor argument (null -> default
//    meta, like the C++ null-profile branch).
//  - The shader-config (.txt) rename of downloadSucceeded is dropped: ShaderPackFolderModel
//    is not ported, and this task only ever runs against the mods folder for now.
//  - The file download runs on the download operation's temp-file + checksum-gated move
//    (client.Downloads.File) instead of the shared download task.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Dependencies;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Tasks;
using FlintLauncher.ExternalApi;
using TruePath;

namespace FlintLauncher.Core.ModPlatform;

public class ResourceDownloadTask : SequentialTask
{
    private readonly IndexedPack _pack;
    private readonly IndexedVersion _packVersion;
    private readonly ResourceFolderModel _packModel;
    private readonly ModrinthClient _modrinth;
    private readonly HttpClient _http;
    private readonly string _downloadReason;
    private readonly string _dependentOn;
    private readonly MinecraftInstance? _instance;

    private readonly LocalResourceUpdateTask? _updateTask;

    // Port of the to_delete tuple: (old metadata name, old file name)
    private (string Name, string Filename) _toDelete = ("", "");

    public ResourceDownloadTask(IndexedPack pack,
        IndexedVersion version,
        ResourceFolderModel packs,
        ModrinthClient modrinth,
        HttpClient http,
        bool isIndexed = true,
        string downloadReason = "standalone",
        string dependentOn = "",
        MinecraftInstance? instance = null)
    {
        _pack = pack;
        _packVersion = version;
        _packModel = packs;
        _modrinth = modrinth;
        _http = http;
        _downloadReason = downloadReason;
        _dependentOn = dependentOn;
        _instance = instance;

        if (isIndexed)
        {
            _updateTask = new LocalResourceUpdateTask(_packModel.IndexDir, _pack, _packVersion);
            _updateTask.HasOldResource += HasOldResource;

            AddTask(_updateTask);
        }

        var download = new CallbackTask(async cancellationToken =>
        {
            EmitStatus($"Downloading resource:\n{_packVersion.DownloadUrl}");
            await _modrinth.DownloadModFileAsync(_http, _packVersion.DownloadUrl,
                (AbsolutePath)_packModel.Dir / GetFilename(),
                CreateModrinthMeta(_instance, _downloadReason, _dependentOn),
                _packVersion.HashType.Length != 0 ? _packVersion.HashType : null,
                _packVersion.Hash.Length != 0 ? _packVersion.Hash : null,
                cancellationToken);
        });
        download.Succeeded += DownloadSucceeded;
        AddTask(download);
    }

    public string GetFilename() => _packVersion.FileName;

    public string GetVersionId() => _packVersion.FileId;

    public IndexedVersion GetVersion() => _packVersion;

    public ResourceProvider GetProvider() => _pack.Provider;

    public string GetName() => _pack.Name;

    public IndexedPack GetPack() => _pack;

    // ------------------------------------------------------------------ internals

    /// <summary>Port of the createModrinthMeta helper (net/ApiHeaderProxy metadata).</summary>
    private static ModrinthDownloadMeta CreateModrinthMeta(MinecraftInstance? instance, string reason, string dependentOn)
    {
        if (instance is null)
        {
            return new ModrinthDownloadMeta(null, null, null, null);
        }

        var profile = instance.GetPackProfile();

        var loaders = profile.GetModLoadersList();

        return new ModrinthDownloadMeta(reason,
            profile.GetComponentVersion("net.minecraft"),
            loaders.Count != 0 ? ModIndex.GetModLoaderAsString(loaders[0]) : "",
            dependentOn);
    }

    // This indirection is done so that we don't delete a mod before being sure it was
    // downloaded successfully!
    private void HasOldResource(string name, string filename)
    {
        _toDelete = (name, filename);
    }

    private void DownloadSucceeded(FlintTask task)
    {
        var oldName = _toDelete.Name;
        var oldFilename = _toDelete.Filename;

        if (oldName.Length == 0 || oldFilename == _packVersion.FileName)
        {
            return;
        }

        _packModel.UninstallResource(oldFilename, preserveMetadata: true);

        // (the C++ also renames the shader config .txt for ShaderPackFolderModel here; that
        //  model is not ported, see the header note)
    }
}
