// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Packwiz;
using Tomlyn.Serialization;

namespace FlintLauncher.Core.Toml;

/// <summary>The source-generated Tomlyn context for the typed TOML readers.</summary>
[TomlSourceGenerationOptions(Converters = new[] { typeof(ModsTomlDependenciesConverter) })]
[TomlSerializable(typeof(PackwizTomlFile))]
[TomlSerializable(typeof(ModsTomlFile))]
[TomlSerializable(typeof(List<ModsTomlDependency>))]
[TomlSerializable(typeof(Dictionary<string, List<ModsTomlDependency>>))]
internal partial class FlintTomlContext : TomlSerializerContext
{
}
