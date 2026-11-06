// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Minecraft.Resources;
using Tomlyn.Serialization;

namespace FlintLauncher.Core.Toml;

/// <summary>
/// Reads the two dependency layouts mods.toml allows: a top-level array of tables, or a
/// table keyed by mod id whose values are arrays of tables.
/// </summary>
internal sealed class ModsTomlDependenciesConverter : TomlConverter<ModsTomlDependencies>
{
    public override ModsTomlDependencies Read(TomlReader reader)
    {
        var resolver = reader.Options.TypeInfoResolver!;
        if (reader.TokenType == TomlTokenType.StartArray)
        {
            var typeInfo = resolver.GetTypeInfo(typeof(List<ModsTomlDependency>), reader.Options)!;
            return new ModsTomlDependencies
            {
                Array = (List<ModsTomlDependency>)typeInfo.ReadAsObject(reader)!,
            };
        }

        if (reader.TokenType == TomlTokenType.StartTable)
        {
            var tableTypeInfo = resolver.GetTypeInfo(
                typeof(Dictionary<string, List<ModsTomlDependency>>), reader.Options)!;
            return new ModsTomlDependencies
            {
                Table = (Dictionary<string, List<ModsTomlDependency>>)tableTypeInfo.ReadAsObject(reader)!,
            };
        }

        // A malformed dependencies value must not discard the rest of the mod metadata.
        reader.Skip();
        return new ModsTomlDependencies();
    }

    public override void Write(TomlWriter writer, ModsTomlDependencies value)
    {
        throw new NotSupportedException("mods.toml dependencies are read-only");
    }
}
