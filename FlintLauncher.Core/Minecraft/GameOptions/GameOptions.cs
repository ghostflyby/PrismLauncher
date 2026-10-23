// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/gameoptions/GameOptions.{h,cpp}: the model behind the instance
// "Game Options" page — a flat <key>:<value> view of an instance's .minecraft/options.txt.
//
// Format semantics kept byte-compatible with the C++:
//  - Lines are split on '\n' only (a trailing '\r' stays part of the value, exactly like
//    QFile::readLine() + chop(1)).
//  - The key/value separator is the FIRST ':' on the line; keys cannot contain ':', values can.
//    Lines without a ':' are skipped. Neither key nor value is unescaped/trimmed.
//  - A "version:<int>" line is pulled out of the item list into Version (a failed parse or a
//    missing line leaves it 0). On save it is written back FIRST, before every item.
//  - Save rewrites the whole file through a QSaveFile-like temporary file + atomic rename, as
//    "key:value\n" lines in UTF-8.
//
// Deviations: the QAbstractListModel surface (rowCount/columnCount/data/headerData) is not
// ported; the item list is exposed directly plus small additive helpers (Find/Set/Remove) that
// operate on the same in-memory list save() writes.

using System.Globalization;
using System.Security;
using System.Text;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Minecraft.GameOptions;

public sealed record GameOptionItem(string Key, string Value);

public sealed class GameOptions
{
    private readonly List<GameOptionItem> _contents = [];

    public GameOptions(AbsolutePath path)
    {
        Path = path;
        Reload();
    }

    /// <summary>The options.txt path (m_path).</summary>
    public AbsolutePath Path { get; }

    /// <summary>Port of isLoaded(): whether the last load opened and parsed the file.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>The "version" line value; 0 when absent (m_version).</summary>
    public int Version { get; private set; }

    /// <summary>The loaded key/value pairs (contents), version line excluded.</summary>
    public IReadOnlyList<GameOptionItem> Contents => _contents;

    /// <summary>Port of rowCount().</summary>
    public int RowCount() => _contents.Count;

    /// <summary>Port of data() column access: key of the row, or null when out of range.</summary>
    public GameOptionItem? At(int row) => row >= 0 && row < _contents.Count ? _contents[row] : null;

    /// <summary>Port of reload(): clears and re-reads the file; keeps the old contents cleared
    /// even when the file cannot be opened (but keeps the previous Version in that case,
    /// mirroring the C++ load() which resets version only after a successful open).</summary>
    public bool Reload()
    {
        _contents.Clear();
        IsLoaded = Load(Path, _contents, out var version);
        if (IsLoaded)
        {
            Version = version;
        }

        return IsLoaded;
    }

    /// <summary>Port of save(): whole-file rewrite ("version:" line first), through a temporary
    /// file renamed over the target (QSaveFile::commit).</summary>
    public bool Save() => Save(Path, _contents, Version);

    // ---- additive data-level edit helpers (operate on the in-memory list; call Save() to write)

    /// <summary>Finds the first item with the given key.</summary>
    public GameOptionItem? Find(string key) => _contents.FirstOrDefault(item => item.Key == key);

    /// <summary>Additive: sets (adding when absent) the value of key.</summary>
    public void Set(string key, string value)
    {
        var index = _contents.FindIndex(item => item.Key == key);
        if (index >= 0)
        {
            _contents[index] = new GameOptionItem(key, value);
        }
        else
        {
            _contents.Add(new GameOptionItem(key, value));
        }
    }

    /// <summary>Additive: removes every item with the given key; true when anything was removed.</summary>
    public bool Remove(string key) => _contents.RemoveAll(item => item.Key == key) > 0;

    // ---------------------------------------------------------------- implementation

    private static bool Load(AbsolutePath path, List<GameOptionItem> contents, out int version)
    {
        version = 0;
        string content;
        try
        {
            content = File.ReadAllText(path.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or SecurityException)
        {
            return false;
        }

        version = 0;
        // QFile::readLine() splits on '\n' and chops only that character: split with
        // StringSplitOptions.None so empty lines still flow through (and get skipped by the
        // ':' lookup), and keep any '\r' inside the value.
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex == -1)
            {
                continue;
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..];
            if (key == "version")
            {
                version = ParseInt(value);
                continue;
            }

            contents.Add(new GameOptionItem(key, value));
        }

        return true;
    }

    /// <summary>QString::toInt() semantics: base 10, optional leading whitespace/sign, no
    /// trailing anything; failure (including overflow) yields 0.</summary>
    private static int ParseInt(string value)
    {
        if (int.TryParse(value, NumberStyles.AllowLeadingWhite | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return 0;
    }

    private static bool Save(AbsolutePath path, List<GameOptionItem> mapping, int version)
    {
        // QSaveFile: the temporary file sits next to the target (same directory, suffixed name)
        var tempPath = path.Parent is { } parent
            ? parent / (path.FileName + ".flint-tmp")
            : new AbsolutePath(path.Value + ".flint-tmp");
        try
        {
            using (var @out = new FileStream(tempPath.Value, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (version != 0)
                {
                    var versionLine = $"version:{version.ToString(CultureInfo.InvariantCulture)}\n";
                    @out.Write(Encoding.UTF8.GetBytes(versionLine));
                }

                foreach (var item in mapping)
                {
                    @out.Write(Encoding.UTF8.GetBytes(item.Key));
                    @out.Write(":"u8);
                    @out.Write(Encoding.UTF8.GetBytes(item.Value));
                    @out.Write("\n"u8);
                }
            }

            tempPath.Move(path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or SecurityException)
        {
            try
            {
                tempPath.Delete();
            }
            catch (Exception)
            {
                // QSaveFile also silently cancels when it cannot clean up.
            }

            return false;
        }
    }
}
