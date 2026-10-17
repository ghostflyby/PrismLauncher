// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/ModDetails.h (ModLicense + ModDetails).
//
// Deviation: QUrl validity ("scheme and host non-empty after stripping optional parens")
// is approximated with Uri.TryCreate(UriKind.Absolute); strings like "www.x.com" (valid
// relative QUrl, but empty scheme/host) are rejected by both.

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of the ModLicense struct.</summary>
public sealed class ModLicense
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";

    public ModLicense() { }

    /// <summary>Port of the single-string parsing constructor.</summary>
    public ModLicense(string license)
    {
        // FIXME: come up with a better license parsing.
        // handle SPDX identifiers? https://spdx.org/licenses/
        var parts = new List<string>(license.Split(' '));
        var notNameParts = new List<string>();
        foreach (var part in parts)
        {
            var candidate = part;
            if (candidate.StartsWith('(') && candidate.EndsWith(')') && candidate.Length >= 2)
            {
                candidate = candidate[1..^1];
            }

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var url) && url.Scheme.Length != 0 && url.Host.Length != 0)
            {
                Url = url.ToString();
                notNameParts.Add(part);
            }
        }

        foreach (var part in notNameParts)
        {
            parts.Remove(part);
        }

        var licensePart = string.Join(' ', parts);
        Name = licensePart;
        Description = licensePart;

        if (parts.Count == 1)
        {
            Id = parts[0];
        }
    }

    public ModLicense(string name, string id, string url, string description)
    {
        Name = name;
        Id = id;
        Url = url;
        Description = description;
    }

    public bool IsEmpty() => Name.Length == 0 && Id.Length == 0 && Url.Length == 0 && Description.Length == 0;
}

/// <summary>Port of the ModDetails struct: the mod-specific details resolved by a parse task.</summary>
public sealed class ModDetails
{
    /// <summary>Mod ID as defined in the ModLoader-specific metadata.</summary>
    public string ModId { get; set; } = "";

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-readable mod version.</summary>
    public string Version { get; set; } = "";

    /// <summary>Human-readable minecraft version.</summary>
    public string McVersion { get; set; } = "";

    /// <summary>URL for mod's home page.</summary>
    public string Homeurl { get; set; } = "";

    /// <summary>Human-readable description.</summary>
    public string Description { get; set; } = "";

    /// <summary>List of the author's names.</summary>
    public List<string> Authors { get; set; } = [];

    /// <summary>Issue tracker URL.</summary>
    public string IssueTracker { get; set; } = "";

    /// <summary>License.</summary>
    public List<ModLicense> Licenses { get; set; } = [];

    /// <summary>Path of mod logo.</summary>
    public string IconFile { get; set; } = "";

    public List<string> Dependencies { get; set; } = [];
}
