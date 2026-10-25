// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2021-2022 Jamie Mansfield <jmansfield@cadixdev.org>
// SPDX-FileCopyrightText: 2022 Jamie Mansfield <jmansfield@cadixdev.org>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of modplatform/technic/SolderPackInstallTask.{h,cpp} (+ SolderPackManifest.{h,cpp} is
// in SolderPackManifest.cs): resolve {solder}/modpack/{pack}/{build}, download every mod file
// (md5-verified), extract all of them into the staging folder's minecraft/ directory and run
// the TechnicPackProcessor (isSolder=true).
//
// Deviation (headless port): the C++ downloads into a QTemporaryDir; the port uses a
// temporary folder under the system temp path that is removed afterwards.

using System.Security.Cryptography;
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.Technic;

public sealed class SolderPackInstallTask(Uri solderUrl, string pack, string version, string minecraftVersion)
    : InstanceTask
{
    private string _minecraftVersion = minecraftVersion;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Resolving modpack files");

        var sourceUrl = TechnicApi.SolderBuildUrl(solderUrl.ToString(), pack, version);
        var request = NetRequest.MakeByteArray(new Uri(sourceUrl));
        var resolveJob = new NetJob("Resolving modpack files");
        resolveJob.AddNetRequest(request);
        await resolveJob.StartAsync(cancellationToken);
        if (!resolveJob.WasSuccessful)
        {
            EmitFailed(resolveJob.FailReason);
            return;
        }

        await FileListSucceeded(request.BodyAsString, cancellationToken);
    }

    private async Task FileListSucceeded(string response, CancellationToken cancellationToken)
    {
        EmitStatus("Downloading modpack");

        SolderPackBuild build;
        try
        {
            build = TechnicApi.LoadSolderPackBuild(response);
        }
        catch (JsonException e)
        {
            EmitFailed("Could not understand pack manifest:\n" + e.Message);
            return;
        }

        if (build.Minecraft.Length != 0)
        {
            _minecraftVersion = build.Minecraft;
        }

        string a1 = $"flint-solder-{Guid.NewGuid():N}";
        var outputDir = Temporary.CreateTempFolder() / a1;
        try
        {
            outputDir.CreateDirectory();

            var filesJob = new NetJob("Downloading modpack");
            var modCount = build.Mods.Count;
            var i = 0;
            foreach (var mod in build.Mods)
            {
                // LoadSolderPackBuild rejects null elements and null urls before this runs
                var modUrl = mod?.Url
                             ?? throw new InvalidOperationException("the Solder build carries an unvalidated mod");
                var path = outputDir / i.ToString();
                var dl = NetRequest.MakeFile(new Uri(modUrl), path);
                if (mod.Md5.Length != 0)
                {
                    dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.MD5, mod.Md5));
                }

                filesJob.AddNetRequest(dl);

                i++;
            }

            await filesJob.StartAsync(cancellationToken);
            if (!filesJob.WasSuccessful)
            {
                EmitFailed(filesJob.FailReason);
                return;
            }

            await DownloadSucceeded(outputDir, modCount, cancellationToken);
        }
        finally
        {
            try
            {
                outputDir.DeleteDirectoryRecursively();
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    private async Task DownloadSucceeded(AbsolutePath outputDir, int modCount, CancellationToken cancellationToken)
    {
        EmitStatus("Extracting modpack");
        var extractDir = StagingPath / "minecraft";
        FileOps.EnsureFolderPathExists(new AbsolutePath(extractDir));

        var i = 0;
        while (modCount > i)
        {
            var path = outputDir / i.ToString();
            if (MmcZip.ExtractDir(path, "", extractDir) is null)
            {
                EmitFailed("Failed to extract modpack");
                return;
            }

            i++;
        }

        // (the "fixing permissions" walk is not ported — see SingleZipPackInstallTask)

        var failure = await TechnicPackProcessor.RunAsync(
            Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution."),
            Name(), Icon, StagingPath, _minecraftVersion, isSolder: true,
            cancellationToken: cancellationToken);
        if (failure is not null)
        {
            EmitFailed(failure);
            return;
        }

        EmitSucceeded();
    }
}
