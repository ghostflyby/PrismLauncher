// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/Mod.{h,cpp}.
//
// Deviations from the C++:
//  - The metadata layer (MetadataHandler::ModStruct) is not ported, so the metadata
//    branches of loaders()/side()/mcVersions()/releaseType()/homepage() are absent; the
//    accessors return the same defaults the C++ produces without metadata (loaders "",
//    side "both", mcVersions [], releaseType "Unknown").
//  - The icon QPixmapCache/Mutex plumbing (Qt GUI) is dropped; only the icon path is
//    kept on ModDetails. LocalModParseTask (the Task wrapper) is not ported — callers
//    can fill ModDetails directly (see finishResolvingWithDetails).

using System.Text.RegularExpressions;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of the Mod class: a Resource with lazily resolved ModDetails.</summary>
public class Mod : Resource
{
    private ModDetails localDetails = new();
    private int requiredByCount;
    private int requiresCount;

    public Mod(string filePath) : this(new FileInfo(filePath)) { }

    public Mod(FileInfo file) : base(file)
    {
        Enabled = file.Extension != ".disabled";
    }

    public ModDetails Details => localDetails;

    public void SetDetails(ModDetails details)
    {
        localDetails = details;
    }

    public override int Compare(Resource other, SortType type = SortType.Name)
    {
        var castOther = other as Mod;
        if (castOther is null)
        {
            return base.Compare(other, type);
        }

        switch (type)
        {
            default:
            case SortType.Enabled:
            case SortType.Name:
            case SortType.Date:
            case SortType.Size:
                return base.Compare(other, type);
            case SortType.Version:
            {
                var thisVer = new McVersion(Version);
                var otherVer = new McVersion(castOther.Version);
                if (thisVer > otherVer)
                {
                    return 1;
                }

                if (thisVer < otherVer)
                {
                    return -1;
                }

                break;
            }
            case SortType.Side:
            {
                var compareResult = string.Compare(Side(), castOther.Side(), StringComparison.OrdinalIgnoreCase);
                if (compareResult != 0)
                {
                    return compareResult;
                }

                break;
            }
            case SortType.McVersions:
            {
                var compareResult = CompareVersionLists(McVersions(), castOther.McVersions());
                if (compareResult != 0)
                {
                    return compareResult;
                }

                break;
            }
            case SortType.Loaders:
            {
                var compareResult = string.Compare(Loaders(), castOther.Loaders(), StringComparison.OrdinalIgnoreCase);
                if (compareResult != 0)
                {
                    return compareResult;
                }

                break;
            }
            case SortType.ReleaseType:
            {
                var compareResult = string.Compare(ReleaseType(), castOther.ReleaseType(), StringComparison.OrdinalIgnoreCase);
                if (compareResult != 0)
                {
                    return compareResult;
                }

                break;
            }
            case SortType.RequiredBy:
            {
                if (RequiredByCount > castOther.RequiredByCount)
                {
                    return 1;
                }

                if (RequiredByCount < castOther.RequiredByCount)
                {
                    return -1;
                }

                break;
            }
            case SortType.Requires:
            {
                if (RequiresCount > castOther.RequiresCount)
                {
                    return 1;
                }

                if (RequiresCount < castOther.RequiresCount)
                {
                    return -1;
                }

                break;
            }
        }

        return 0;
    }

    /// <summary>Port of the compareVersionLists helper from Mod.cpp (element-wise McVersion order).</summary>
    private static int CompareVersionLists(IReadOnlyList<string> leftVersions, IReadOnlyList<string> rightVersions)
    {
        var commonSize = Math.Min(leftVersions.Count, rightVersions.Count);

        for (var i = 0; i < commonSize; i++)
        {
            var leftVersion = new McVersion(leftVersions[i].Trim());
            var rightVersion = new McVersion(rightVersions[i].Trim());

            if (leftVersion > rightVersion)
            {
                return 1;
            }

            if (leftVersion < rightVersion)
            {
                return -1;
            }
        }

        if (leftVersions.Count > rightVersions.Count)
        {
            return 1;
        }

        if (leftVersions.Count < rightVersions.Count)
        {
            return -1;
        }

        return 0;
    }

    public override bool ApplyFilter(Regex filter)
    {
        if (filter.IsMatch(Description()))
        {
            return true;
        }

        foreach (var author in Authors())
        {
            if (filter.IsMatch(author))
            {
                return true;
            }
        }

        return base.ApplyFilter(filter);
    }

    /// <summary>Port of Mod::name (details name falling back to the file-derived name).</summary>
    public override string Name => Details.Name.Length != 0 ? Details.Name : base.Name;

    public string ModId()
    {
        var dModId = Details.ModId;
        if (dModId.Length != 0)
        {
            return dModId;
        }

        return Name;
    }

    public string Version => Details.Version;

    public override string Homepage() => Details.Homeurl;

    public string Description() => Details.Description;

    public List<string> Authors() => Details.Authors;

    public List<ModLicense> Licenses() => Details.Licenses;

    public string IssueTracker() => Details.IssueTracker;

    /// <summary>Without the metadata layer this returns the same empty default as the C++.</summary>
    public string Loaders() => "";

    /// <summary>Port of Mod::side: the metadata-less default is the universal side ("both").</summary>
    public string Side() => "both";

    public List<string> McVersions() => [];

    public string McVersionsString() => string.Join(", ", McVersions());

    /// <summary>Port of Mod::releaseType: the metadata-less default is "Unknown".</summary>
    public string ReleaseType() => "Unknown";

    /// <summary>Internal path to the mod's icon file.</summary>
    public string IconPath => localDetails.IconFile;

    public override bool Valid => localDetails.ModId.Length != 0;

    public List<string> Dependencies() => Details.Dependencies;

    public int RequiredByCount => requiredByCount;

    public int RequiresCount => requiresCount;

    public void SetRequiredByCount(int value)
    {
        requiredByCount = value;
    }

    public void SetRequiresCount(int value)
    {
        requiresCount = value;
    }

    /// <summary>Port of Mod::finishResolvingWithDetails (minus the pixmap-cache bookkeeping).</summary>
    public void FinishResolvingWithDetails(ModDetails details)
    {
        SetResolving(false, ResolutionTicket);
        SetResolved();
        localDetails = details;
    }
}
