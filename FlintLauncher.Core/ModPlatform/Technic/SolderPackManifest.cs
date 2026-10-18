// SPDX-License-Identifier: GPL-3.0-only
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

    public required List<SolderPackBuildMod> Mods { get; init; }
}
