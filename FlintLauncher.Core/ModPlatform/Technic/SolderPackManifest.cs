// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Jamie Mansfield <jmansfield@cadixdev.org>
//
// Port of modplatform/technic/SolderPackManifest.{h,cpp}: the Solder API models —
// /modpack/{slug} (recommended/latest/builds) and /modpack/{slug}/{build} (minecraft + mods).
// The wire shape is annotated on the model properties; the Solder members are all required
// except a mod's version (toString() of a missing/junk value -> "", the coercing converter).

using System.Text.Json.Serialization;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Technic;

public sealed class SolderPack
{
    public required string Recommended { get; set; }

    public required string Latest { get; set; }

    public required List<string> Builds { get; init; }
}

public sealed class SolderPackBuildMod
{
    public required string Name { get; set; }

    // optional: toString() of a missing/junk value -> ""
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Version { get; set; } = "";

    public required string Md5 { get; set; }

    public required string Url { get; set; }
}

public sealed class SolderPackBuild
{
    public required string Minecraft { get; set; }

    /// <summary>Elements are nullable on the wire (a JSON null mod element is surfaced by
    /// the loader's "Value is not an object" check — the pinned Solder behavior).</summary>
    public required List<SolderPackBuildMod?> Mods { get; init; }
}
