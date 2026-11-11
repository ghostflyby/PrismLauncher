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
// The imgur response envelope of launcher/screenshots/ImgurAPI (the RPC::Sink payloads of
// the image upload and the album creation).

using System.Text.Json.Serialization;

namespace FlintLauncher.ExternalApi.Imgur;

/// <summary>The imgur response envelope {"success": true, "data": {...}} — shared by the
/// image upload and album creation replies.</summary>
public sealed record ImgurResponseJson(
    bool? Success,
    ImgurDataJson? Data);

public sealed record ImgurDataJson(
    string? Id,
    string? Link,
    // all-lowercase on the wire — camelCase would produce "deleteHash"
    [property: JsonPropertyName("deletehash")]
    string? DeleteHash);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImgurResponseJson))]
public sealed partial class ImgurJsonContext : JsonSerializerContext;
