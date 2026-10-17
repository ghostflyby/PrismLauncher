// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaVersion.{h,cpp}.

using System.Globalization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Text;

namespace FlintLauncher.Core.Java;

public sealed partial class JavaVersion : IComparable<JavaVersion>, IEquatable<JavaVersion>
{
    private readonly string _string;
    private bool parseable;

    public JavaVersion() : this("") { }

    public JavaVersion(string rhs)
    {
        _string = rhs;
        Parse();
    }

    public JavaVersion(int major, int minor, int security, int build = 0, string? name = null)
    {
        _string = "";
        Major = major;
        Minor = minor;
        Security = security;
        Name = name ?? "";
        parseable = true;

        var versions = new List<string>();
        if (build != 0)
        {
            Prerelease = build.ToString();
            versions.Insert(0, Prerelease);
        }

        if (Security != 0)
        {
            versions.Insert(0, Security.ToString());
        }
        else if (versions.Count > 0)
        {
            versions.Insert(0, "0");
        }

        if (Minor != 0)
        {
            versions.Insert(0, Minor.ToString());
        }
        else if (versions.Count > 0)
        {
            versions.Insert(0, "0");
        }

        versions.Insert(0, Major.ToString());
        _string = string.Join(".", versions);
        Parse();
    }

    private void Parse()
    {
        // Same patterns as the C++ port: unanchored first match, like QRegularExpression::match.
        var pattern = _string.StartsWith("1.", StringComparison.Ordinal) ? WithOne() : WithoutOne();
        var match = pattern.Match(_string);
        parseable = match.Success;
        Major = GetCapturedInteger(match, "major");
        Minor = GetCapturedInteger(match, "minor");
        Security = GetCapturedInteger(match, "security");
        Prerelease = match.Groups["prerelease"].Success ? match.Groups["prerelease"].Value : "";
    }

    private static int GetCapturedInteger(Match match, string what)
    {
        var str = match.Groups[what].Success ? match.Groups[what].Value : "";
        if (str.Length == 0)
        {
            return 0;
        }

        return int.Parse(str, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"1[.](?<major>[0-9]+)([.](?<minor>[0-9]+))?(_(?<security>[0-9]+)?)?(-(?<prerelease>[a-zA-Z0-9]+))?")]
    private static partial Regex WithOne();

    [GeneratedRegex(@"(?<major>[0-9]+)([.](?<minor>[0-9]+))?([.](?<security>[0-9]+))?(-(?<prerelease>[a-zA-Z0-9]+))?")]
    private static partial Regex WithoutOne();

    public override string ToString() => _string;

    public int Major { get; private set; }

    public int Minor { get; private set; }

    public int Security { get; private set; }

    public string Name { get; private set; } = "";

    public string Prerelease { get; private set; } = "";

    /// <summary>Starting from Java 18, UTF-8 is the default charset: https://openjdk.org/jeps/400</summary>
    public bool DefaultsToUtf8() => parseable && Major >= 18;

    public bool IsModular() => parseable && Major >= 9;

    public int CompareTo(JavaVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (this < other)
        {
            return -1;
        }

        if (other < this)
        {
            return 1;
        }

        return 0;
    }

    public bool Equals(JavaVersion? other)
    {
        if (other is null)
        {
            return false;
        }

        if (parseable && other.parseable)
        {
            return Major == other.Major && Minor == other.Minor && Security == other.Security && Prerelease == other.Prerelease;
        }

        return string.Equals(_string, other._string, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as JavaVersion);

    public override int GetHashCode() =>
        parseable ? HashCode.Combine(Major, Minor, Security, Prerelease) : _string.GetHashCode(StringComparison.Ordinal);

    public static bool operator <(JavaVersion left, JavaVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.parseable && right.parseable)
        {
            if (left.Major < right.Major)
            {
                return true;
            }

            if (left.Major > right.Major)
            {
                return false;
            }

            if (left.Minor < right.Minor)
            {
                return true;
            }

            if (left.Minor > right.Minor)
            {
                return false;
            }

            if (left.Security < right.Security)
            {
                return true;
            }

            if (left.Security > right.Security)
            {
                return false;
            }

            // everything else being equal, consider prerelease status
            var thisPre = left.Prerelease.Length > 0;
            var rhsPre = right.Prerelease.Length > 0;
            if (thisPre && !rhsPre)
            {
                // this is a prerelease and the other one isn't -> lesser
                return true;
            }

            if (!thisPre && rhsPre)
            {
                // this isn't a prerelease and the other one is -> greater
                return false;
            }

            if (thisPre && rhsPre)
            {
                // both are prereleases - use natural compare
                return StringUtils.NaturalCompare(left.Prerelease, right.Prerelease) < 0;
            }

            // neither is prerelease, so they are the same -> this cannot be less than rhs
            return false;
        }

        return StringUtils.NaturalCompare(left._string, right._string) < 0;
    }

    public static bool operator >(JavaVersion left, JavaVersion right) => !(left < right) && !(left == right);

    public static bool operator ==(JavaVersion? left, JavaVersion? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(JavaVersion? left, JavaVersion? right) => !(left == right);
}
