// SPDX-License-Identifier: GPL-3.0-only
// The name-derivation helpers of launcher/FileSystem.{h,cpp}: sanitizing user strings into
// file names (FS::RemoveInvalidPathChars) and picking a free name on disk
// (FS::DirNameFromString, FS::getUniqueResourceName). Names travel as strings; the folders
// they are probed against are TruePath AbsolutePaths.

using System.Globalization;
using System.Text;
using TruePath;

namespace FlintLauncher.Core.IO;

public static class FileNaming
{
    private const string BadChars = "<>:\"|?*\r\n!";

    /// <summary>Port of FS::RemoveInvalidPathChars: separators are printable and kept.</summary>
    public static string RemoveInvalidPathChars(string str, char replaceWith = '-')
    {
        return RemoveChars(str, replaceWith);
    }

    /// <summary>
    /// Port of FS::DirNameFromString(string, QStringList inDirs): derive a directory name
    /// that clashes with none of the given dirs (#6110 dedupes instance folder names across
    /// every configured instances directory).
    /// </summary>
    public static string DirNameFromString(string str, params IEnumerable<AbsolutePath> inDirs)
    {
        var num = 0;
        var baseName = RemoveChars(str, '-', "\\/"); // FS::RemoveInvalidFilenameChars
        string dirName;
        do
        {
            dirName = num == 0 ? baseName : baseName + "(" + num + ")";

            // If it's over 9000
            if (num > 9000)
            {
                return "";
            }

            num++;
        } while (inDirs.Any(dir => Path.Exists((dir / dirName).Value)));

        return dirName;
    }

    /// <summary>
    /// Port of FS::getUniqueResourceName: a disabled resource ("x.disabled") whose enabled name
    /// is taken gets the next free "x.duplicateN" name next to it; anything else is returned
    /// unchanged (enabled resources take priority).
    /// </summary>
    public static AbsolutePath GetUniqueResourceName(AbsolutePath filePath)
    {
        const string disabledSuffix = ".disabled";
        var fileName = filePath.FileName;
        if (!fileName.EndsWith(disabledSuffix, StringComparison.Ordinal))
        {
            return filePath;
        }

        var dir = filePath.Parent ?? new AbsolutePath("");
        if (!Path.Exists((dir / fileName[..^disabledSuffix.Length]).Value))
        {
            return filePath;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var counter = 1;
        AbsolutePath candidate;
        do
        {
            candidate = dir / (counter == 1 ? baseName + ".duplicate" : baseName + ".duplicate" + counter);
            counter++;
        } while (Path.Exists(candidate.Value));

        return candidate;
    }

    /// <summary>QChar::isPrint equivalent (format/surrogate/private-use/unassigned are not printable; space is).</summary>
    private static bool IsPrint(char c)
    {
        var category = char.GetUnicodeCategory(c);
        return category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);
    }

    private static string RemoveChars(string source, char replace, string extraChars = "")
    {
        var badChars = BadChars + extraChars;
        var sb = new StringBuilder(source.Length);
        foreach (var c in source)
        {
            if (c < 0x20 || !IsPrint(c) || badChars.Contains(c))
            {
                sb.Append(replace);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
