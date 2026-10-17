// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/Resource.{h,cpp} (plus the ResourceType/ResourceStatus/
// SortType/EnableAction enums and the StringUtils::humanReadableFileSize helper it uses).
//
// Deviations from the C++:
//  - The metadata system (MetadataHandler/Metadata::ModStruct, modplatform layer) is not
//    ported yet, so m_metadata and the accessors that read it (name()/provider()/homepage()
//    metadata branches, setMetadata, destroyMetadata's index lookup) are dropped; provider
//    is always the "Unknown" fallback and homepage is empty. Resource::updateIssues depends
//    on MinecraftInstance and is dropped with the m_issues list.
//  - Hard-link counting needs stat(2) st_nlink which System.IO does not expose; the count
//    is fixed at 1, so IsMoreThanOneHardLink is always false (symlink checks are kept).
//  - Resource::destroy's FS::trash path is not portable; Destroy always deletes directly
//    (the folder model calls it with attemptTrash=false anyway).
//  - String comparisons use StringComparison.OrdinalIgnoreCase where Qt uses
//    Qt::CaseInsensitive and CurrentCulture for localeAwareCompare.

using System.Globalization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of ResourceType.</summary>
public enum ResourceType : byte
{
    /// <summary>Indicates an unspecified resource type.</summary>
    Unknown,

    /// <summary>The resource is a zip file containing the resource's class files.</summary>
    ZipFile,

    /// <summary>The resource is a single file (not a zip file).</summary>
    SingleFile,

    /// <summary>The resource is in a folder on the filesystem.</summary>
    Folder,

    /// <summary>The resource is a litemod.</summary>
    LiteMod,
}

/// <summary>Port of ResourceStatus.</summary>
public enum ResourceStatus : byte
{
    /// <summary>Both JAR and Metadata are present.</summary>
    Installed,

    /// <summary>Only the Metadata is present.</summary>
    NotInstalled,

    /// <summary>Only the JAR is present.</summary>
    NoMetadata,

    /// <summary>Default status.</summary>
    Unknown,
}

/// <summary>Port of SortType (the sorting keys used by the resource list proxies).</summary>
public enum SortType : byte
{
    Name,
    Date,
    Version,
    Enabled,
    PackFormat,
    Provider,
    Size,
    Side,
    McVersions,
    Loaders,
    ReleaseType,
    Requires,
    RequiredBy,
    Filename,
}

/// <summary>Port of EnableAction.</summary>
public enum EnableAction : byte
{
    Enable,
    Disable,
    Toggle,
}

/// <summary>
/// Port of the Resource class: general managed resource mirroring a file on disk, with
/// some more info for display and house-keeping purposes. Subclass it to add additional
/// data / behavior, such as Mods or Resource packs.
/// </summary>
public partial class Resource
{
    public Resource(string filePath) : this(new FileInfo(filePath)) { }

    public Resource(FileInfo fileInfo)
    {
        Fileinfo = fileInfo;
    }

    public FileInfo Fileinfo
    {
        get;
        set
        {
            field = value;
            ParseFile();
        }
    }

    public DateTime DateTimeChanged { get; private set; }

    public string InternalId { get; private set; } = "";

    public ResourceType Type { get; private set; } = ResourceType.Unknown;

    public bool Enabled { get; protected set; } = true;

    public string SizeStr { get; private set; } = "";

    public long SizeInfo { get; private set; }

    public virtual string Name => name;

    public virtual bool Valid => Type != ResourceType.Unknown;

    public ResourceStatus Status { get; private set; } = ResourceStatus.Unknown;

    public void SetStatus(ResourceStatus status)
    {
        Status = status;
    }

    /// <summary>Port of Resource::shouldResolve (the parse-task bookkeeping).</summary>
    public bool ShouldResolve => !IsResolving && !IsResolved;

    public bool IsResolving { get; private set; }

    public bool IsResolved { get; private set; }

    public int ResolutionTicket { get; private set; }

    public void SetResolving(bool resolving, int resolutionTicket)
    {
        IsResolving = resolving;
        ResolutionTicket = resolutionTicket;
    }

    /// <summary>Marks the pending parse as concluded (C++ sets m_isResolved in finishResolvingWithDetails).</summary>
    protected void SetResolved()
    {
        IsResolved = true;
    }

    public string GetOriginalFileName()
    {
        var fileName = Fileinfo.Name;
        if (!Enabled)
        {
            fileName = fileName[..^9]; // chop ".disabled"
        }

        return fileName;
    }

    public void ParseFile()
    {
        var fileName = Fileinfo.Name;

        Type = ResourceType.Unknown;

        InternalId = fileName;

        (SizeStr, SizeInfo) = CalculateFileSize(Fileinfo);
        if (Directory.Exists(Fileinfo.FullName) && !File.Exists(Fileinfo.FullName))
        {
            Type = ResourceType.Folder;
            name = fileName;
        }
        else if (File.Exists(Fileinfo.FullName))
        {
            if (fileName.EndsWith(".disabled", StringComparison.Ordinal))
            {
                fileName = fileName[..^9];
                Enabled = false;
            }

            if (fileName.EndsWith(".zip", StringComparison.Ordinal) || fileName.EndsWith(".jar", StringComparison.Ordinal))
            {
                Type = ResourceType.ZipFile;
                fileName = fileName[..^4];
            }
            else if (fileName.EndsWith(".nilmod", StringComparison.Ordinal))
            {
                Type = ResourceType.ZipFile;
                fileName = fileName[..^7];
            }
            else if (fileName.EndsWith(".litemod", StringComparison.Ordinal))
            {
                Type = ResourceType.LiteMod;
                fileName = fileName[..^8];
            }
            else
            {
                Type = ResourceType.SingleFile;
            }

            name = fileName;
        }

        DateTimeChanged = LastModified(Fileinfo);
    }

    /// <summary>Port of Resource::provider: without the metadata layer this is always the fallback.</summary>
    public string Provider() => "Unknown";

    /// <summary>Port of Resource::homepage: without the metadata layer there is no meta URL.</summary>
    public virtual string Homepage() => "";

    /// <summary>
    /// Port of Resource::compare: compares two Resources, for sorting purposes, considering
    /// an ascending order, returning &gt; 0 when 'this' comes after 'other', 0 when equal,
    /// and &lt; 0 when 'this' comes before 'other'.
    /// </summary>
    public virtual int Compare(Resource other, SortType type = SortType.Name)
    {
        switch (type)
        {
            default:
            case SortType.Enabled:
                if (Enabled && !other.Enabled)
                {
                    return 1;
                }

                if (!Enabled && other.Enabled)
                {
                    return -1;
                }

                break;
            case SortType.Name:
            {
                var thisName = Name;
                var otherName = other.Name;

                // TODO do we need this? it could result in 0 being returned
                RemoveThePrefix(ref thisName);
                RemoveThePrefix(ref otherName);

                return string.Compare(thisName, otherName, StringComparison.OrdinalIgnoreCase);
            }
            case SortType.Date:
                if (DateTimeChanged > other.DateTimeChanged)
                {
                    return 1;
                }

                if (DateTimeChanged < other.DateTimeChanged)
                {
                    return -1;
                }

                break;
            case SortType.Filename:
                return string.Compare(Fileinfo.Name, other.Fileinfo.Name, StringComparison.CurrentCulture);
            case SortType.Size:
            {
                if (Type != other.Type)
                {
                    if (Type == ResourceType.Folder)
                    {
                        return -1;
                    }

                    if (other.Type == ResourceType.Folder)
                    {
                        return 1;
                    }
                }

                if (SizeInfo > other.SizeInfo)
                {
                    return 1;
                }

                if (SizeInfo < other.SizeInfo)
                {
                    return -1;
                }

                break;
            }
            case SortType.Provider:
            {
                var compareResult = string.Compare(Provider(), other.Provider(), StringComparison.OrdinalIgnoreCase);
                if (compareResult != 0)
                {
                    return compareResult;
                }

                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// Port of Resource::applyFilter: returns whether the given filter includes the
    /// resource (true), or should filter it out (false).
    /// </summary>
    public virtual bool ApplyFilter(Regex filter)
    {
        if (filter.IsMatch(Name))
        {
            return true;
        }

        if (filter.IsMatch(Fileinfo.Name))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Port of Resource::enable: changes the enabled property (by renaming the file with a
    /// ".disabled" suffix), according to 'action'. Returns whether a change was applied.
    /// </summary>
    public bool Enable(EnableAction action)
    {
        if (Type == ResourceType.Unknown || Type == ResourceType.Folder)
        {
            return false;
        }

        var path = Fileinfo.FullName;

        var enable = action switch
        {
            EnableAction.Enable => true,
            EnableAction.Disable => false,
            _ => !Enabled, // Toggle
        };

        if (Enabled == enable)
        {
            return false;
        }

        if (enable)
        {
            // m_enabled is false, but there's no '.disabled' suffix.
            // TODO: Report error?
            if (!path.EndsWith(".disabled", StringComparison.Ordinal))
            {
                return false;
            }

            path = path[..^9];
        }
        else
        {
            path += ".disabled";
            if (File.Exists(path) || Directory.Exists(path))
            {
                path = FileSystem.GetUniqueResourceName(path);
            }
        }

        try
        {
            File.Move(Fileinfo.FullName, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return false;
        }

        Fileinfo = new FileInfo(path);

        Enabled = enable;
        return true;
    }

    /// <summary>
    /// Port of Resource::destroy: delete all files of this resource. The indexDir /
    /// preserveMetadata parameters only matter to the (unported) metadata system; the C++
    /// trash path is replaced by a direct delete.
    /// </summary>
    public bool Destroy(bool attemptTrash = true)
    {
        Type = ResourceType.Unknown;
        _ = attemptTrash; // no portable trash; always delete directly
        return FileSystem.DeletePath(Fileinfo.FullName);
    }

    public bool IsSymLink => (File.GetAttributes(Fileinfo.FullName) & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Port of Resource::isSymLinkUnder: checks if the file pointed to by the resource is a
    /// symlink or under a symlink in the given instance path.
    /// </summary>
    public bool IsSymLinkUnder(string instPath)
    {
        if (IsSymLink)
        {
            return true;
        }

        var relAbsPath = Path.GetRelativePath(Path.GetFullPath(instPath), Fileinfo.FullName);
        var relCanonPath = Path.GetRelativePath(Path.GetFullPath(instPath), CanonicalFilePath(Fileinfo.FullName));

        return relAbsPath != relCanonPath;
    }

    public bool IsMoreThanOneHardLink => false; // hard-link counts are not portable (see header note)

    // ------------------------------------------------------------------ internals

    /* The file corresponding to this resource. */
    /* The cached date when this file was last changed. */
    /* Internal ID for internal purposes. Properties such as human-readability should not be assumed. */
    /* Name as reported via the file name. In the absence of a better name, this is shown to the user. */
    private string name = "";
    /* The type of file we're dealing with. */
    /* Installation status of the resource. */
    /* Used to keep track of pending / concluded parse actions on the resource. */

    /// <summary>QFileInfo::canonicalFilePath: the path with symlinks resolved (best effort).</summary>
    private static string CanonicalFilePath(string path)
    {
        try
        {
            var finalTarget = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return finalTarget is null ? Path.GetFullPath(path) : finalTarget.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>Port of the calculateFileSize helper from Resource.cpp.</summary>
    private static (string Str, long Info) CalculateFileSize(FileInfo file)
    {
        if (Directory.Exists(file.FullName) && !File.Exists(file.FullName))
        {
            var count = Directory.GetFileSystemEntries(file.FullName).Length;
            var str = count != 1 ? "items" : "item";
            return ($"{count.ToString(CultureInfo.InvariantCulture)} {str}", count);
        }

        var size = File.Exists(file.FullName) ? file.Length : 0;
        return (HumanReadableFileSize(size, useSi: true), size);
    }

    private static DateTime LastModified(FileInfo file)
    {
        try
        {
            return File.GetLastWriteTime(file.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Port of the removeThePrefix helper from Resource.cpp.</summary>
    private static void RemoveThePrefix(ref string s)
    {
        s = ThePrefixRegex().Replace(s, "");
        s = s.Trim();
    }

    [GeneratedRegex("^(?:the|teh) +", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThePrefixRegex();

    /// <summary>
    /// Port of StringUtils::humanReadableFileSize (kept local: Text/StringUtils.cs has no
    /// call site for it yet and only Resources/ may be touched here).
    /// </summary>
    private static string HumanReadableFileSize(double bytes, bool useSi, int decimalPoints = 1)
    {
        string[] units = useSi ? ["KB", "MB", "GB", "TB"] : ["KiB", "MiB", "GiB", "TiB"];
        var scale = useSi ? 1000 : 1024;

        int u = -1;
        var r = Math.Pow(10, decimalPoints);

        do
        {
            bytes /= scale;
            u++;
        } while (Math.Round(Math.Abs(bytes) * r) / r >= scale && u < units.Length - 1);

        return bytes.ToString("F2", CultureInfo.InvariantCulture) + " " + units[u];
    }
}
