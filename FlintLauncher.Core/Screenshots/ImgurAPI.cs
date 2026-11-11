// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
// Port of launcher/screenshots/ImgurAPI.{h,cpp} (the unified imgur request layer that
// replaced ImgurUpload.{h,cpp} + ImgurAlbumCreation.{h,cpp}): upload one screenshot to
// imgur, and group uploaded screenshots into an album through their delete hashes.
// Upstream exposes ImgurAPI::makeUpload / ImgurAPI::makeAlbum returning (request, result)
// pairs; here each pair is an object whose async method performs the exchange and fills
// the result. The requests and the reply rules are the imgur client's operations
// (FlintLauncher.ExternalApi.Imgur); these units own the screenshot side — reading the file
// and carrying the reply into the ScreenShot metadata.

using System.Security;
using FlintLauncher.Core.Net;
using FlintLauncher.ExternalApi.Imgur;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Screenshots;

/// <summary>Port of ImgurAPI::AlbumResult.</summary>
public sealed record ImgurAlbumResult
{
    public string DeleteHash { get; set; } = "";
    public string Id { get; set; } = "";
}

/// <summary>The upload half of ImgurAPI::makeUpload (the Net::Request + RPC::Sink&lt;QString&gt;
/// pair): fills the ScreenShot metadata from the reply.</summary>
public sealed class ImgurUpload(ScreenShot shot, ImgurClient imgur, HttpClient http)
{
    /// <summary>Uploads the screenshot; on success it carries the imgur id, url and delete
    /// hash. An unreadable screenshot fails as <see cref="InvalidOperationException"/>, the
    /// upload itself as <see cref="ImgurApiException"/>.</summary>
    public async Task UploadAsync(CancellationToken cancellationToken = default)
    {
        byte[] fileData;
        try
        {
            fileData = await shot.FilePath.ReadAllBytesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or SecurityException)
        {
            throw new InvalidOperationException($"Could not open file {shot.FilePath} for reading: {e.Message}");
        }

        var data = await imgur.Images.Upload(fileData, shot.FileName(), shot.BaseName())
            .SendOnceAsync(http, cancellationToken).ConfigureAwait(false);
        if (data is not null)
        {
            shot.ImgurId = data.Id ?? "";
            shot.Url = data.Link ?? "";
            shot.ImgurDeleteHash = data.DeleteHash ?? "";
        }
    }
}

/// <summary>The album half of ImgurAPI::makeAlbum (the Net::Request + RPC::Sink&lt;AlbumResult&gt;
/// pair): posts the uploaded screenshots' delete hashes.</summary>
public sealed class ImgurAlbumCreation(
    ImgurAlbumResult result,
    IReadOnlyList<ScreenShot> screenshots,
    ImgurClient imgur,
    HttpClient http)
{
    /// <summary>Creates the album; on success <c>result</c> carries its delete hash and id. A
    /// failure surfaces as <see cref="ImgurApiException"/>.</summary>
    public async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        var hashes = screenshots.Select(shot => shot.ImgurDeleteHash).ToArray();
        var data = await imgur.Albums.Create(hashes).SendOnceAsync(http, cancellationToken).ConfigureAwait(false);
        if (data is not null)
        {
            result.DeleteHash = data.DeleteHash ?? "";
            result.Id = data.Id ?? "";
        }
    }
}
