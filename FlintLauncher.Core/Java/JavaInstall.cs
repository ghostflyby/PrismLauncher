// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaInstall.{h,cpp}: a discovered Java installation (path + JvmVersion
// + architecture). JavaInstall descends from BaseVersion upstream — the trivial BaseVersion
// accessors (descriptor/name/typeString) are inlined here and the BaseVersion overloads of the
// comparison operators collapse onto the typed ones (the only polymorphic sibling in the java
// layer is Java::Metadata, which is ordered independently).

using FlintLauncher.Core.Text;
using JdkFind;

namespace FlintLauncher.Core.Java;

public sealed class JavaInstall(JvmVersion id, string arch, string path) : IComparable<JavaInstall>, IEquatable<JavaInstall>
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

    /// <summary>Port of JavaInstall::operator&lt;: architecture, then version id, then path.</summary>
    public bool LessThan(JavaInstall rhs)
    {
        var archCompare = StringUtils.NaturalCompare(Arch, rhs.Arch, ignoreCase: true);
        if (archCompare != 0)
        {
            return archCompare < 0;
        }

        if (Id < rhs.Id)
        {
            return true;
        }

        if (rhs.Id < Id)
        {
            return false;
        }

        return StringUtils.NaturalCompare(Path, rhs.Path, ignoreCase: true) < 0;
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

    /// <summary>Port of JavaInstall::operator&gt;.</summary>
    public bool GreaterThan(JavaInstall rhs) => !LessThan(rhs) && !Equals(rhs);

    public int CompareTo(JavaInstall? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (LessThan(other))
        {
            return -1;
        }

        if (other.LessThan(this))
        {
            return 1;
        }

        return 0;
    }

    public override bool Equals(object? obj) => Equals(obj as JavaInstall);

    public override int GetHashCode() => HashCode.Combine(Arch, Id, Path);

    public static bool operator <(JavaInstall left, JavaInstall right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.LessThan(right);
    }

    public static bool operator >(JavaInstall left, JavaInstall right) => left.GreaterThan(right);

    public static bool operator ==(JavaInstall? left, JavaInstall? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(JavaInstall? left, JavaInstall? right) => !(left == right);
}
