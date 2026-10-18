// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/ModIndex.{h,cpp} (+ modplatform/ResourceType.h) and the data half of
// modplatform/ResourceAPI.h: the provider-neutral mod metadata models. Pure data layer —
// the Qt models/signals of the original are not ported.
//
// Deviations:
//  - ModPlatform::ModLoaderType is the one already ported in FlintLauncher.Core.Minecraft
//    (P2b); QFlags<ModLoaderType> maps to the [Flags] enum itself.
//  - The EnumWrapper<T> types (SideType/IndexedVersionType/DependencyType) become readonly
//    record structs over the raw enum + mapping functions. fromString stays case-sensitive
//    like the C++ (DependencyType upper-cases its input first, exactly like upstream).
//  - QVariant ids become strings ("" = null QVariant); the Flame write path parses them
//    back to integers like the C++ toInt() calls.

using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.ModPlatform;

/// <summary>Port of ModPlatform::ResourceProvider.</summary>
public enum ResourceProvider : byte
{
    Modrinth = 0,
    Flame = 1,
}

/// <summary>Port of ModPlatform::ResourceType (modplatform/ResourceType.h).</summary>
public enum ResourceType : byte
{
    Mod,
    ResourcePack,
    ShaderPack,
    Modpack,
    DataPack,
    World,
    Screenshots,
    TexturePack,
    Unknown,
}

/// <summary>Port of ModPlatform::DependencyTypeValue (+ the EnumWrapper mapping).</summary>
public enum DependencyTypeValue : byte
{
    Required,
    Optional,
    Incompatible,
    Embedded,
    Tool,
    Include,
    Unknown,
}

/// <summary>Port of the DependencyType EnumWrapper.</summary>
public readonly record struct DependencyType(DependencyTypeValue Value)
{
    public static DependencyType Invalid => new(DependencyTypeValue.Unknown);

    public bool IsValid => Value != DependencyTypeValue.Unknown;

    public static string ToString(DependencyTypeValue value) => value switch
    {
        DependencyTypeValue.Required => "REQUIRED",
        DependencyTypeValue.Optional => "OPTIONAL",
        DependencyTypeValue.Incompatible => "INCOMPATIBLE",
        DependencyTypeValue.Embedded => "EMBEDDED",
        DependencyTypeValue.Tool => "TOOL",
        DependencyTypeValue.Include => "INCLUDE",
        _ => "UNKNOWN",
    };

    public override string ToString() => ToString(Value);

    public static DependencyType FromString(string str)
    {
        var upper = str.ToUpperInvariant();
        foreach (DependencyTypeValue value in Enum.GetValues<DependencyTypeValue>())
        {
            if (upper == ToString(value))
            {
                return new DependencyType(value);
            }
        }

        return Invalid;
    }
}

/// <summary>Port of ModPlatform::SideTypeValue.</summary>
[Flags]
public enum SideTypeValue : byte
{
    NoSide = 0,
    ClientSide = 1 << 0,
    ServerSide = 1 << 1,
    UniversalSide = ClientSide | ServerSide,
}

/// <summary>Port of the SideType EnumWrapper.</summary>
public readonly record struct SideType(SideTypeValue Value)
{
    public static SideType Invalid => new(SideTypeValue.NoSide);

    public bool IsValid => Value != SideTypeValue.NoSide;

    public static string ToString(SideTypeValue value) => value switch
    {
        SideTypeValue.ClientSide => "client",
        SideTypeValue.ServerSide => "server",
        SideTypeValue.UniversalSide => "both",
        _ => "",
    };

    public override string ToString() => ToString(Value);

    public static SideType FromString(string str)
    {
        foreach (SideTypeValue value in Enum.GetValues<SideTypeValue>())
        {
            if (str == ToString(value))
            {
                return new SideType(value);
            }
        }

        return Invalid;
    }
}

/// <summary>Port of ModPlatform::IndexedVersionTypeValue.</summary>
public enum IndexedVersionTypeValue : byte
{
    Unknown = 0,
    Release = 1,
    Beta = 2,
    Alpha = 3,
}

/// <summary>Port of the IndexedVersionType EnumWrapper.</summary>
public readonly record struct IndexedVersionType(IndexedVersionTypeValue Value)
{
    public static IndexedVersionType Invalid => new(IndexedVersionTypeValue.Unknown);

    public bool IsValid => Value != IndexedVersionTypeValue.Unknown;

    public static string ToString(IndexedVersionTypeValue value) => value switch
    {
        IndexedVersionTypeValue.Release => "Release",
        IndexedVersionTypeValue.Beta => "Beta",
        IndexedVersionTypeValue.Alpha => "Alpha",
        _ => "Unknown",
    };

    public override string ToString() => ToString(Value);

    public static IndexedVersionType FromString(string str)
    {
        // case-insensitive since #3404 (IndexedVersionType::fromString)
        foreach (IndexedVersionTypeValue value in Enum.GetValues<IndexedVersionTypeValue>())
        {
            if (string.Equals(str, ToString(value), StringComparison.OrdinalIgnoreCase))
            {
                return new IndexedVersionType(value);
            }
        }

        return Invalid;
    }

    /// <summary>Port of IndexedVersionType::fromStringList: the invalid entries are dropped.</summary>
    public static List<IndexedVersionType> FromStringList(IEnumerable<string> list)
    {
        var result = new List<IndexedVersionType>();
        foreach (var str in list)
        {
            var type = FromString(str);
            if (type.IsValid)
            {
                result.Add(type);
            }
        }

        return result;
    }

    /// <summary>Port of IndexedVersionType::toModrinth: the Modrinth version_type spelling
    /// ("" for the invalid/unknown entry).</summary>
    public string ToModrinth() => Value switch
    {
        IndexedVersionTypeValue.Release => "release",
        IndexedVersionTypeValue.Beta => "beta",
        IndexedVersionTypeValue.Alpha => "alpha",
        _ => "",
    };

    /// <summary>Port of IndexedVersionType::toModrinthList: the empty spellings are dropped.</summary>
    public static List<string> ToModrinthList(IReadOnlyList<IndexedVersionType> types)
    {
        var result = new List<string>();
        foreach (var type in types)
        {
            var s = type.ToModrinth();
            if (s.Length != 0)
            {
                result.Add(s);
            }
        }

        return result;
    }
}

/// <summary>Port of ModPlatform::ProviderCapabilities.</summary>
public static class ProviderCapabilities
{
    public static string Name(ResourceProvider p) => p switch
    {
        ResourceProvider.Modrinth => "modrinth",
        ResourceProvider.Flame => "curseforge",
        _ => "",
    };

    public static string ReadableName(ResourceProvider p) => p switch
    {
        ResourceProvider.Modrinth => "Modrinth",
        ResourceProvider.Flame => "CurseForge",
        _ => "",
    };

    public static IReadOnlyList<string> HashType(ResourceProvider p) => p switch
    {
        ResourceProvider.Modrinth => ["sha512", "sha1"],
        ResourceProvider.Flame => ["sha1", "md5", "murmur2"], // Try newer formats first, fall back to old format
        _ => [],
    };
}

/// <summary>Port of ModPlatform::ModpackAuthor.</summary>
public sealed class ModpackAuthor
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>Port of ModPlatform::DonationData.</summary>
public sealed class DonationData
{
    public string Id { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>Port of ModPlatform::Dependency.</summary>
public sealed class Dependency
{
    /// <summary>QVariant addonId: a string (possibly numeric for Flame) or "" for null.</summary>
    public string AddonId { get; set; } = "";

    public DependencyType Type { get; set; } = DependencyType.Invalid;
    public string Version { get; set; } = "";
}

/// <summary>Port of ModPlatform::IndexedVersion.</summary>
public sealed class IndexedVersion
{
    public string AddonId { get; set; } = "";
    public string FileId { get; set; } = "";
    public string Version { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public IndexedVersionType VersionType { get; set; } = IndexedVersionType.Invalid;
    public List<string> McVersion { get; set; } = [];
    public string DownloadUrl { get; set; } = "";
    public string Date { get; set; } = "";
    public string FileName { get; set; } = "";
    public ModLoaderType Loaders { get; set; } = ModLoaderType.None;
    public string HashType { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool IsPreferred { get; set; } = true;
    public string Changelog { get; set; } = "";
    public List<Dependency> Dependencies { get; set; } = [];
    public SideType Side { get; set; } = SideType.Invalid; // this is for flame API

    // For internal use, not provided by APIs
    public bool IsCurrentlySelected { get; set; }

    public string GetVersionDisplayString()
    {
        var releaseType = VersionType.IsValid ? $" [{VersionType}]" : "";
        var versionStr = !Version.Contains(VersionNumber, StringComparison.Ordinal) ? VersionNumber : "";
        var gameVersion = "";
        foreach (var v in McVersion)
        {
            if (Version.Contains(v, StringComparison.Ordinal))
            {
                gameVersion = "";
                break;
            }

            if (gameVersion.Length == 0)
            {
                gameVersion = $" for {v}";
            }
        }

        return $"{Version}{gameVersion} — {versionStr}{releaseType}";
    }
}

/// <summary>Port of ModPlatform::ExtraPackData.</summary>
public sealed class ExtraPackData
{
    public List<DonationData> Donate { get; } = [];

    public string IssuesUrl { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string WikiUrl { get; set; } = "";
    public string DiscordUrl { get; set; } = "";

    public string Status { get; set; } = "";

    public string Body { get; set; } = "";
}

/// <summary>Port of ModPlatform::IndexedPack.</summary>
public sealed class IndexedPack
{
    public string AddonId { get; set; } = "";
    public ResourceProvider Provider { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ModpackAuthor> Authors { get; set; } = [];
    public string LogoName { get; set; } = "";
    public string LogoUrl { get; set; } = "";
    public string WebsiteUrl { get; set; } = "";
    public SideType Side { get; set; } = SideType.Invalid;

    public bool VersionsLoaded { get; set; }
    public List<IndexedVersion> Versions { get; } = [];

    // Don't load by default, since some modplatform don't have that info
    public bool ExtraDataLoaded { get; set; } = true;
    public ExtraPackData ExtraData { get; } = new();

    public ResourceType ResourceType { get; set; } = ResourceType.Unknown;

    // For internal use, not provided by APIs
    public bool IsVersionSelected(int index)
    {
        if (!VersionsLoaded)
        {
            return false;
        }

        return Versions[index].IsCurrentlySelected;
    }

    public bool IsAnyVersionSelected()
    {
        if (!VersionsLoaded)
        {
            return false;
        }

        return Versions.Any(v => v.IsCurrentlySelected);
    }
}

/// <summary>Port of ModPlatform::OverrideDep.</summary>
public sealed record OverrideDep(string Quilt, string Fabric, string Slug, ResourceProvider Provider);

public static class ModIndex
{
    /// <summary>Port of ModPlatform::getOverrideDeps().</summary>
    public static List<OverrideDep> GetOverrideDeps() =>
    [
        new(Quilt: "634179", Fabric: "306612", Slug: "API", Provider: ResourceProvider.Flame),
        new(Quilt: "720410", Fabric: "308769", Slug: "KotlinLibraries", Provider: ResourceProvider.Flame),

        new(Quilt: "qvIfYCYJ", Fabric: "P7dR8mSH", Slug: "API", Provider: ResourceProvider.Modrinth),
        new(Quilt: "lwVhp9o5", Fabric: "Ha28R6CL", Slug: "KotlinLibraries", Provider: ResourceProvider.Modrinth),
    ];

    public static string GetMetaUrl(ResourceProvider provider, string projectId)
    {
        return (provider == ResourceProvider.Flame ? "https://www.curseforge.com/projects/" : "https://modrinth.com/mod/") + projectId;
    }

    /// <summary>Port of ModPlatform::getModLoaderAsString.</summary>
    public static string GetModLoaderAsString(ModLoaderType type) => type switch
    {
        ModLoaderType.NeoForge => "neoforge",
        ModLoaderType.Forge => "forge",
        ModLoaderType.Cauldron => "cauldron",
        ModLoaderType.LiteLoader => "liteloader",
        ModLoaderType.Fabric => "fabric",
        ModLoaderType.Quilt => "quilt",
        ModLoaderType.DataPack => "datapack",
        ModLoaderType.Babric => "babric",
        ModLoaderType.Bta => "bta-babric",
        ModLoaderType.LegacyFabric => "legacy-fabric",
        ModLoaderType.Ornithe => "ornithe",
        ModLoaderType.Rift => "rift",
        _ => "",
    };

    /// <summary>Port of ModPlatform::getModLoaderFromString.</summary>
    public static ModLoaderType GetModLoaderFromString(string type) => type switch
    {
        "neoforge" => ModLoaderType.NeoForge,
        "forge" => ModLoaderType.Forge,
        "cauldron" => ModLoaderType.Cauldron,
        "liteloader" => ModLoaderType.LiteLoader,
        "fabric" => ModLoaderType.Fabric,
        "quilt" => ModLoaderType.Quilt,
        "babric" => ModLoaderType.Babric,
        "bta-babric" => ModLoaderType.Bta,
        "legacy-fabric" => ModLoaderType.LegacyFabric,
        "ornithe" => ModLoaderType.Ornithe,
        "rift" => ModLoaderType.Rift,
        _ => ModLoaderType.None,
    };

    /// <summary>Port of ModPlatform::modLoaderTypesToList (same loader order as the C++ list).</summary>
    public static List<ModLoaderType> ModLoaderTypesToList(ModLoaderType flags)
    {
        ModLoaderType[] loaderList =
        [
            ModLoaderType.NeoForge, ModLoaderType.Forge, ModLoaderType.Cauldron, ModLoaderType.LiteLoader, ModLoaderType.Quilt,
            ModLoaderType.Fabric, ModLoaderType.Babric, ModLoaderType.Bta, ModLoaderType.LegacyFabric, ModLoaderType.Ornithe,
            ModLoaderType.Rift,
        ];
        var flagList = new List<ModLoaderType>();
        foreach (var flag in loaderList)
        {
            if ((flags & flag) != 0)
            {
                flagList.Add(flag);
            }
        }

        return flagList;
    }

    /// <summary>Port of ModPlatform::hasSingleModLoaderSelected.</summary>
    public static bool HasSingleModLoaderSelected(ModLoaderType l)
    {
        var x = (ushort)l;
        return x != 0 && (x & (x - 1)) == 0;
    }
}

/// <summary>Port of ModPlatform::Category.</summary>
public sealed record Category(string Name, string Id);
