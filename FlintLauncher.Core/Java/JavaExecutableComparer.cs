// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Text;
using JdkFind;

namespace FlintLauncher.Core.Java;

/// <summary>The upstream JavaInstall ordering — architecture, then version id, then path —
/// applied to JdkFind's executable view.</summary>
public sealed class JavaExecutableComparer : IComparer<JavaExecutable>
{
    public static JavaExecutableComparer Instance { get; } = new();

    public int Compare(JavaExecutable? x, JavaExecutable? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var archCompare = Math.Sign(StringUtils.NaturalCompare(x.Architecture, y.Architecture, ignoreCase: true));
        if (archCompare != 0)
        {
            return archCompare;
        }

        var versionCompare = x.Version.CompareTo(y.Version);
        if (versionCompare != 0)
        {
            return versionCompare;
        }

        return Math.Sign(StringUtils.NaturalCompare(x.Path, y.Path, ignoreCase: true));
    }
}
