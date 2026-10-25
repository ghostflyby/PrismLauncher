// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
//
// Port of launcher/java/JavaInstall.{h,cpp}: a discovered Java installation (path + JvmVersion
// + architecture). JavaInstall descends from BaseVersion upstream — the trivial BaseVersion
// accessors (descriptor/name/typeString) are inlined here and the BaseVersion overloads of the
// comparison operators collapse onto the typed ones (the only polymorphic sibling in the java
// layer is Java::Metadata, which is ordered independently).

using System.Numerics;
using FlintLauncher.Core.Text;
using JdkFind;

namespace FlintLauncher.Core.Java;

public sealed class JavaInstall(JvmVersion id, string arch, string path) : IComparable<JavaInstall>, IEquatable<JavaInstall>, IComparisonOperators<JavaInstall, JavaInstall, bool>
{
    public JavaInstall() : this(JvmVersion.Unknown(), "unknown", "") { }

    public JavaInstall(string id, string arch, string path) : this(JvmVersion.Parse(id), arch, path) { }

    /// <summary>Port of BaseVersion::descriptor().</summary>
    public string Descriptor() => Id.ToString();

    /// <summary>Port of BaseVersion::name().</summary>
    public string Name() => Id.ToString();

    /// <summary>Port of BaseVersion::typeString().</summary>
    public string TypeString() => Arch;

    public JvmVersion Id { get; } = id;

    /// <summary>The Java SE feature version JdkFind read from the installation; null for a
    /// hand-built entry (GetDefaultJava) or an installation without a readable release file.</summary>
    public int? LanguageVersion { get; init; }

    public string Arch { get; } = arch;

    public string Path { get; } = path;

    public bool Is64Bit { get; init; }

    /// <summary>The JavaInstall ordering as a trichotomy — the port of JavaInstall::operator&lt;:
    /// architecture, then version id, then path.</summary>
    public int CompareTo(JavaInstall? other)
    {
        if (other is null)
        {
            return 1;
        }

        var archCompare = Math.Sign(StringUtils.NaturalCompare(Arch, other.Arch, ignoreCase: true));
        if (archCompare != 0)
        {
            return archCompare;
        }

        var idCompare = Id.CompareTo(other.Id);
        if (idCompare != 0)
        {
            return idCompare;
        }

        return Math.Sign(StringUtils.NaturalCompare(Path, other.Path, ignoreCase: true));
    }

    /// <summary>Port of JavaInstall::operator==.</summary>
    public bool Equals(JavaInstall? other)
    {
        if (other is null)
        {
            return false;
        }

        return Arch == other.Arch && Id == other.Id && Path == other.Path;
    }

    public override bool Equals(object? obj) => Equals(obj as JavaInstall);

    public override int GetHashCode() => HashCode.Combine(Arch, Id, Path);

    public static bool operator <(JavaInstall left, JavaInstall right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(JavaInstall left, JavaInstall right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(JavaInstall left, JavaInstall right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(JavaInstall left, JavaInstall right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) >= 0;
    }

    public static bool operator ==(JavaInstall? left, JavaInstall? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(JavaInstall? left, JavaInstall? right) => !(left == right);
}
