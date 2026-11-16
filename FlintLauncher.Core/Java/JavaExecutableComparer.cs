// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Globalization;
using JdkFind;

namespace FlintLauncher.Core.Java;

/// <summary>The upstream JavaInstall ordering — architecture, then version id, then path —
/// applied to JdkFind's executable view.</summary>
public sealed class JavaExecutableComparer : IComparer<JavaExecutable>
{
    private static readonly StringComparer NumericIgnoreCase = StringComparer.Create(
        CultureInfo.InvariantCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);

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

        var archCompare = NumericIgnoreCase.Compare(x.Architecture, y.Architecture);
        if (archCompare != 0)
        {
            return archCompare;
        }

        var versionCompare = x.Version.CompareTo(y.Version);
        if (versionCompare != 0)
        {
            return versionCompare;
        }

        return NumericIgnoreCase.Compare(x.Path, y.Path);
    }
}
