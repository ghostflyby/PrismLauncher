// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of modplatform/legacy_ftb/PrivatePackManager.{h,cpp}: the persisted set of private
// FTB pack codes, one code per line in "private_packs.txt" (the C++ keeps the file name
// relative to the launcher's working directory; the path is injectable here so tests can
// point it at a temp dir). Saves are skipped unless the set changed since the load (dirty).

using System.Security;
using System.Text;
using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

public sealed class PrivatePackManager(string? filename = null)
{
    private readonly string _filename = filename ?? "private_packs.txt";
    private readonly HashSet<string> _currentPacks = new(StringComparer.Ordinal);
    private bool _dirty;

    public bool Empty => _currentPacks.Count == 0;

    public IReadOnlyCollection<string> GetCurrentPackCodes() => _currentPacks;

    /// <summary>Port of PrivatePackManager::load: one code per line; unreadable/missing
    /// files yield an empty set (the C++ only warns).</summary>
    public void Load()
    {
        try
        {
            var content = Encoding.UTF8.GetString(File.ReadAllBytes(_filename));
            _currentPacks.Clear();
            foreach (var code in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _currentPacks.Add(code);
            }

            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            _currentPacks.Clear();
        }
    }

    /// <summary>Port of PrivatePackManager::save: the codes joined by '\n'; a no-op unless
    /// the set changed (dirty). Write failures are swallowed like the C++ (warn only).</summary>
    public void Save()
    {
        if (!_dirty)
        {
            return;
        }

        try
        {
            FileOps.Write(new AbsolutePath(_filename), Encoding.UTF8.GetBytes(string.Join('\n', _currentPacks)));
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    public void Add(string code)
    {
        _currentPacks.Add(code);
        _dirty = true;
    }

    public bool Remove(string code)
    {
        _dirty = true;
        return _currentPacks.Remove(code);
    }
}
