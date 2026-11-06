// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using Tomlyn.Serialization;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>The mods.toml / neoforge.mods.toml document read by <see cref="ModUtils.ReadMcModToml"/>.</summary>
internal sealed class ModsTomlFile
{
    [TomlPropertyName("mods")]
    public List<ModsTomlMod>? Mods { get; set; }

    [TomlPropertyName("authors")]
    public string? Authors { get; set; }

    [TomlPropertyName("displayURL")]
    public string? DisplayUrl { get; set; }

    [TomlPropertyName("issueTrackerURL")]
    public string? IssueTrackerUrl { get; set; }

    [TomlPropertyName("license")]
    public string? License { get; set; }

    [TomlPropertyName("logoFile")]
    public string? LogoFile { get; set; }

    [TomlPropertyName("dependencies")]
    public ModsTomlDependencies? Dependencies { get; set; }
}

internal sealed class ModsTomlMod
{
    [TomlPropertyName("modId")]
    public string? ModId { get; set; }

    [TomlPropertyName("version")]
    public string? Version { get; set; }

    [TomlPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [TomlPropertyName("description")]
    public string? Description { get; set; }

    [TomlPropertyName("authors")]
    public string? Authors { get; set; }

    [TomlPropertyName("displayURL")]
    public string? DisplayUrl { get; set; }

    [TomlPropertyName("issueTrackerURL")]
    public string? IssueTrackerUrl { get; set; }

    [TomlPropertyName("license")]
    public string? License { get; set; }

    [TomlPropertyName("logoFile")]
    public string? LogoFile { get; set; }
}

/// <summary>
/// The two shapes mods.toml uses for dependencies: a top-level array of tables, or a table
/// keyed by the depending mod id whose values are arrays of tables. The converter fills one.
/// </summary>
internal sealed class ModsTomlDependencies
{
    public List<ModsTomlDependency>? Array { get; init; }

    public Dictionary<string, List<ModsTomlDependency>>? Table { get; init; }
}

internal sealed class ModsTomlDependency
{
    [TomlPropertyName("modId")]
    public string? ModId { get; set; }

    [TomlPropertyName("type")]
    public string? Type { get; set; }

    [TomlPropertyName("mandatory")]
    public bool Mandatory { get; set; }
}
