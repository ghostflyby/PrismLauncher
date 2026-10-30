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
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.Technic;

public sealed class SolderPackInstallTask(Uri solderUrl, string pack, string version, string minecraftVersion, HttpClient http)
    : InstanceTask
{
    private string _minecraftVersion = minecraftVersion;

    internal override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Resolving modpack files");

        var sourceUrl = TechnicApi.SolderBuildUrl(solderUrl.ToString(), pack, version);
        string listing;
        try
        {
            listing = await FlintHttp.GetStringAsync(http,
                new HttpRequestMessage(HttpMethod.Get, sourceUrl), cancellationToken);
        }
        catch (FlintHttpException e)
        {
            throw new InvalidOperationException(e.Reason);
        }

        await FileListSucceeded(listing, cancellationToken);
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
            throw new InvalidOperationException("Could not understand pack manifest:\n" + e.Message);
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

            var downloads = new List<HttpDownloadTask>();
            var modCount = build.Mods.Count;
            var i = 0;
            foreach (var mod in build.Mods)
            {
                // LoadSolderPackBuild rejects null elements and null urls before this runs
                var modUrl = mod?.Url
                             ?? throw new InvalidOperationException("the Solder build carries an unvalidated mod");
                var path = outputDir / i.ToString();
                var dl = HttpDownloadTask.ToFile(new Uri(modUrl), path, http);
                if (mod.Md5.Length != 0)
                {
                    dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.MD5, mod.Md5));
                }

                downloads.Add(dl);

                i++;
            }

            await TaskWork.RunAllAsync(downloads,
                    (dl, token) => dl.DownloadAsync(null, token), maxConcurrent: 6, cancellationToken)
                .ConfigureAwait(false);

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
                throw new InvalidOperationException("Failed to extract modpack");
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
            throw new InvalidOperationException(failure);
        }
    }
}
