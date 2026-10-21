// SPDX-License-Identifier: GPL-3.0-only
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
//  - The NetJob status line is raised on this task (the C# NetJob has no public status
//    setter); progress propagates through the SequentialTask subtask plumbing.

using System.Security.Cryptography;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Dependencies;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;

namespace FlintLauncher.Core.ModPlatform;

public class ResourceDownloadTask : SequentialTask
{
    private readonly IndexedPack _pack;
    private readonly IndexedVersion _packVersion;
    private readonly ResourceFolderModel _packModel;

    private NetJob? _filesNetJob;
    private readonly LocalResourceUpdateTask? _updateTask;

    // Port of the to_delete tuple: (old metadata name, old file name)
    private (string Name, string Filename) _toDelete = ("", "");

    public ResourceDownloadTask(IndexedPack pack,
        IndexedVersion version,
        ResourceFolderModel packs,
        bool isIndexed = true,
        string downloadReason = "standalone",
        string dependentOn = "",
        MinecraftInstance? instance = null,
        ModrinthApi? modrinth = null)
    {
        _pack = pack;
        _packVersion = version;
        _packModel = packs;
        var modrinth1 = modrinth ?? new ModrinthApi();

        if (isIndexed)
        {
            _updateTask = new LocalResourceUpdateTask(_packModel.IndexDir, _pack, _packVersion);
            _updateTask.HasOldResource += HasOldResource;

            AddTask(_updateTask);
        }

        _filesNetJob = new NetJob("Resource download");
        EmitStatus($"Downloading resource:\n{_packVersion.DownloadUrl}");

        var action = modrinth1.MakeFileRequest(_packVersion.DownloadUrl,
            Path.Combine(_packModel.Dir, GetFilename()),
            CreateModrinthMeta(instance, downloadReason, dependentOn));
        if (_packVersion.HashType.Length != 0 && _packVersion.Hash.Length != 0)
        {
            var validator = MakeChecksumValidator(_packVersion.HashType, _packVersion.Hash);
            if (validator is not null)
            {
                action.AddValidator(validator);
            }
        }

        _filesNetJob.AddNetRequest(action);
        _filesNetJob.Succeeded += DownloadSucceeded;
        _filesNetJob.Failed += DownloadFailed;
        _filesNetJob.Progress += DownloadProgressChanged;

        AddTask(_filesNetJob);
    }

    public string GetFilename() => _packVersion.FileName;

    public string GetVersionId() => _packVersion.FileId;

    public IndexedVersion GetVersion() => _packVersion;

    public ResourceProvider GetProvider() => _pack.Provider;

    public string GetName() => _pack.Name;

    public IndexedPack GetPack() => _pack;

    // ------------------------------------------------------------------ internals

    /// <summary>Port of the createModrinthMeta helper (net/ApiHeaderProxy metadata).</summary>
    private static ModrinthApi.DownloadMeta CreateModrinthMeta(MinecraftInstance? instance, string reason, string dependentOn)
    {
        if (instance is null)
        {
            return default;
        }

        var profile = instance.GetPackProfile();

        var loaders = profile.GetModLoadersList();

        return new ModrinthApi.DownloadMeta(reason,
            profile.GetComponentVersion("net.minecraft"),
            loaders.Count != 0 ? ModIndex.GetModLoaderAsString(loaders[0]) : "",
            dependentOn);
    }

    /// <summary>
    /// Port of the ChecksumValidator switch: md4/md5 -> MD5 (there is no managed MD4 in
    /// .NET, see HashUtils), sha1/sha256/sha512 map directly; anything else (murmur2,
    /// unknown) downloads without a checksum validator, like the C++ default branch.
    /// </summary>
    private static ChecksumValidator? MakeChecksumValidator(string hashType, string hash)
    {
        return HashUtils.AlgorithmFromString(hashType) switch
        {
            Algorithm.Md4 => ChecksumValidator.FromHex(HashAlgorithmName.MD5, hash),
            Algorithm.Md5 => ChecksumValidator.FromHex(HashAlgorithmName.MD5, hash),
            Algorithm.Sha1 => ChecksumValidator.FromHex(HashAlgorithmName.SHA1, hash),
            Algorithm.Sha256 => ChecksumValidator.FromHex(HashAlgorithmName.SHA256, hash),
            Algorithm.Sha512 => ChecksumValidator.FromHex(HashAlgorithmName.SHA512, hash),
            _ => null,
        };
    }

    // This indirection is done so that we don't delete a mod before being sure it was
    // downloaded successfully!
    private void HasOldResource(string name, string filename)
    {
        _toDelete = (name, filename);
    }

    private void DownloadSucceeded(FlintTask task)
    {
        _filesNetJob = null;
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

    private void DownloadFailed(FlintTask task, string reason)
    {
        _filesNetJob = null;
        EmitFailed(reason);
    }

    private void DownloadProgressChanged(FlintTask task, TaskStepProgress progress)
    {
        SetProgress(progress.Current, progress.Total);
    }
}
