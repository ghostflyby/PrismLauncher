// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2020-2021 Jamie Mansfield <jmansfield@cadixdev.org>
//     Copyright 2021 Petr Mrazek <peterix@gmail.com>
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
// Port of modplatform/atlauncher/ATLPackIndex.{h,cpp}: the packsnew.json index model
// (IndexedPack/IndexedVersion) and its parser. The safeName is the C++ normalized pack
// image name ("[^A-Za-z0-9]" stripped, lowercased, ".png" appended). The wire shape is
// annotated on the model properties; deserialization goes through the AtLauncherJsonContext
// (AtLauncherJson.cs).

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FlintLauncher.ExternalApi.AtLauncher;

public sealed class IndexedVersion
{
    public required string Version { get; set; }

    public required string Minecraft { get; set; }
}

public sealed class IndexedPack
{
    public required int Id { get; set; }

    public required int Position { get; set; }

    public required string Name { get; set; }

    // the wire value is the raw "private"/"public" text; PackType stays the computed view
    [JsonPropertyName("type")]
    public required string TypeRaw { get; set; }

    public required List<IndexedVersion> Versions { get; init; }

    public bool System { get; set; }

    public string? Description { get; set; }

    // [JsonIgnore]: with the naming policy the computed getter's name ("type") would
    // collide with TypeRaw's explicit wire name
    [JsonIgnore]
    public PackType Type => TypeRaw == "private" ? PackType.Private : PackType.Public;

    public string SafeName => AtLauncherPackIndex.SafePackName(Name).ToLowerInvariant() + ".png";
}

public static partial class AtLauncherPackIndex
{
    /// <summary>The "[^A-Za-z0-9]" -> "" replacement shared with the install task's packSafeName.</summary>
    public static string SafePackName(string name) => UnsafeCharsRegex().Replace(name, "");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex UnsafeCharsRegex();
}
