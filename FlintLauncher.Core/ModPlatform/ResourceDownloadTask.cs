// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2023 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// Port of launcher/ResourceDownloadTask.{h,cpp}: download an IndexedVersion's primary file
// into the target folder model's directory and keep the per-mod metadata (.pw.toml) in
// sync. The metadata update runs first (when indexed) and the file download second; the old
// file is only uninstalled after the new download succeeded.
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
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Modrinth;
using TruePath;

namespace FlintLauncher.Core.ModPlatform;

public class ResourceDownloadTask
{
    private readonly IndexedPack _pack;
    private readonly IndexedVersion _packVersion;
    private readonly ResourceFolderModel _packModel;
    private readonly ModrinthClient _modrinth;
    private readonly HttpClient _http;
    private readonly string _downloadReason;
    private readonly string _dependentOn;
    private readonly MinecraftInstance? _instance;

    private readonly bool _isIndexed;

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
        _isIndexed = isIndexed;
    }

    public string GetFilename() => _packVersion.FileName;

    public string GetVersionId() => _packVersion.FileId;

    public IndexedVersion GetVersion() => _packVersion;

    public ResourceProvider GetProvider() => _pack.Provider;

    public string GetName() => _pack.Name;

    public IndexedPack GetPack() => _pack;

    /// <summary>Downloads the primary file and keeps the index entry in sync. Returning is
    /// success; any other exception is a failure whose message is the reason.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var previous = _isIndexed
            ? await LocalResourceUpdateTask.RunAsync(_packModel.IndexDir, _pack, _packVersion,
                cancellationToken).ConfigureAwait(false)
            : null;

        if (_packVersion.DownloadUrl is not { } downloadUrl)
        {
            throw new InvalidOperationException($"The version {_packVersion.FileId} has no download URL.");
        }

        await _modrinth.Downloads.File(downloadUrl,
                CreateModrinthMeta(_instance, _downloadReason, _dependentOn),
                HashUtils.VerifiedChecksum(_packVersion.HashType, _packVersion.Hash))
            .SendTo(_http, HttpCompletionOption.ResponseHeadersRead,
                (_packModel.Dir / GetFilename()).Value, cancellationToken)
            .ConfigureAwait(false);

        // the new file is in place: the previous one can go. The metadata pass ran before the
        // download, so a failed download leaves the old file untouched.
        if (previous is not { Name.Length: > 0 } old || old.Filename == _packVersion.FileName)
        {
            return;
        }

        _packModel.UninstallResource(old.Filename, preserveMetadata: true);

        // (the C++ also renames the shader config .txt for ShaderPackFolderModel here; that
        //  model is not ported, see the header note)
    }

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
}
