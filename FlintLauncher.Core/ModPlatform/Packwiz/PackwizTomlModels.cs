// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using Tomlyn.Serialization;

namespace FlintLauncher.Core.ModPlatform.Packwiz;

/// <summary>The .pw.toml document read by <see cref="Packwiz.GetIndexForMod"/>.</summary>
internal sealed class PackwizTomlFile
{
    [TomlRequired]
    [TomlPropertyName("name")]
    public required string Name { get; set; }

    [TomlRequired]
    [TomlPropertyName("filename")]
    public required string Filename { get; set; }

    [TomlPropertyName("side")]
    public string? Side { get; set; }

    [TomlPropertyName("x-prismlauncher-release-type")]
    public string? ReleaseType { get; set; }

    [TomlPropertyName("x-prismlauncher-loaders")]
    public List<string>? Loaders { get; set; }

    [TomlPropertyName("x-prismlauncher-mc-versions")]
    public List<string>? McVersions { get; set; }

    [TomlPropertyName("x-prismlauncher-version-number")]
    public string? VersionNumber { get; set; }

    [TomlPropertyName("x-prismlauncher-lock-update")]
    public bool LockUpdate { get; set; }

    [TomlPropertyName("x-prismlauncher-dependencies")]
    public List<PackwizTomlDependency>? Dependencies { get; set; }

    [TomlRequired]
    [TomlPropertyName("download")]
    public required PackwizTomlDownload Download { get; set; }

    [TomlPropertyName("update")]
    public PackwizTomlUpdate? Update { get; set; }
}

internal sealed class PackwizTomlDownload
{
    [TomlPropertyName("mode")]
    public string? Mode { get; set; }

    [TomlPropertyName("url")]
    public string? Url { get; set; }

    [TomlPropertyName("hash-format")]
    public string? HashFormat { get; set; }

    [TomlPropertyName("hash")]
    public string? Hash { get; set; }
}

internal sealed class PackwizTomlUpdate
{
    [TomlPropertyName("curseforge")]
    public PackwizTomlFlameUpdate? CurseForge { get; set; }

    [TomlPropertyName("modrinth")]
    public PackwizTomlModrinthUpdate? Modrinth { get; set; }
}

internal sealed class PackwizTomlFlameUpdate
{
    [TomlRequired]
    [TomlPropertyName("file-id")]
    public required long FileId { get; set; }

    [TomlRequired]
    [TomlPropertyName("project-id")]
    public required long ProjectId { get; set; }
}

internal sealed class PackwizTomlModrinthUpdate
{
    [TomlRequired]
    [TomlPropertyName("mod-id")]
    public required string ModId { get; set; }

    [TomlRequired]
    [TomlPropertyName("version")]
    public required string Version { get; set; }
}

internal sealed class PackwizTomlDependency
{
    [TomlRequired]
    [TomlPropertyName("addonId")]
    public required string AddonId { get; set; }

    [TomlPropertyName("version")]
    public string? Version { get; set; }

    [TomlPropertyName("type")]
    public string? Type { get; set; }
}
