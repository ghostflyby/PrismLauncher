// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/legacy_ftb/PrivatePackManager.{h,cpp}: the persisted set of private
// FTB pack codes, one code per line in "private_packs.txt" (the C++ keeps the file name
// relative to the launcher's working directory; the path is injectable here so tests can
// point it at a temp dir). Saves are skipped unless the set changed since the load (dirty).

using System.Security;
using System.Text;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

public sealed class PrivatePackManager
{
    private readonly string _filename;
    private readonly HashSet<string> _currentPacks = new(StringComparer.Ordinal);
    private bool dirty;

    public PrivatePackManager(string? filename = null)
    {
        _filename = filename ?? "private_packs.txt";
    }

    public bool Empty => _currentPacks.Count == 0;

    public IReadOnlyCollection<string> GetCurrentPackCodes() => _currentPacks;

    /// <summary>Port of PrivatePackManager::load: one code per line; unreadable/missing
    /// files yield an empty set (the C++ only warns).</summary>
    public void Load()
    {
        try
        {
            var content = Encoding.UTF8.GetString(FileSystem.Read(_filename));
            _currentPacks.Clear();
            foreach (var code in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _currentPacks.Add(code);
            }

            dirty = false;
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                      or SecurityException)
        {
            _currentPacks.Clear();
        }
    }

    /// <summary>Port of PrivatePackManager::save: the codes joined by '\n'; a no-op unless
    /// the set changed (dirty). Write failures are swallowed like the C++ (warn only).</summary>
    public void Save()
    {
        if (!dirty)
        {
            return;
        }

        try
        {
            FileSystem.Write(_filename, Encoding.UTF8.GetBytes(string.Join('\n', _currentPacks)));
            dirty = false;
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                      or SecurityException) { }
    }

    public void Add(string code)
    {
        _currentPacks.Add(code);
        dirty = true;
    }

    public bool Remove(string code)
    {
        dirty = true;
        return _currentPacks.Remove(code);
    }
}
