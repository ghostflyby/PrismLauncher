// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/Version.{h,cpp} — FlexVer ordering (https://git.sleeping.town/exa/FlexVer)
// with Prism's custom handling of spaces ("1.20 Pre-Release 1").
// Renamed from `Version` to `McVersion` to avoid ambiguity with System.Version.

using System.Text;

namespace FlintLauncher.Core.Versioning;

public sealed class McVersion : IComparable<McVersion>, IEquatable<McVersion>
{
    private enum SectionType : byte
    {
        Null,
        Textual,
        Numeric,
        PreRelease,
    }

    private readonly struct Section(SectionType type, string value)
    {
        public SectionType Type { get; } = type;
        public string Value { get; } = value;
    }

    private readonly string _string;
    private Section[] _sections = [];

    public McVersion() : this("") { }

    public McVersion(string str)
    {
        _string = str;
        Parse();
    }

    public override string ToString() => _string;

    public bool IsEmpty => _string.Length == 0;

    private void Parse()
    {
        var parsed = new List<Section>();
        var len = _string.Length;
        for (var i = 0; i < len;)
        {
            var type = SectionType.Textual;
            var value = new StringBuilder();
            var c = _string[i];
            if (c == '+')
            {
                break; // Ignore appendices
            }

            // Custom: the space is special, to handle strings like "1.20 Pre-Release 1" (Modrinth versions).
            if (c == '-' || c == ' ')
            {
                value.Append(c);
                i++;
                // If the next char is non-digit, mark as pre-release (requires >= 1 non-digit after
                // the dash/space so the component has length > 1).
                if (i < len && !char.IsDigit(_string[i]))
                {
                    type = SectionType.PreRelease;
                }
            }
            else if (char.IsDigit(c))
            {
                type = SectionType.Numeric;
            }

            for (; i < len; i++)
            {
                var r = _string[i];
                if (char.IsDigit(r) != (type == SectionType.Numeric) // starts a new section
                    || (r == ' ' && type == SectionType.Numeric)     // numeric section then a space is a pre-release
                    || (r == '-' && type != SectionType.PreRelease)  // "---" is a valid pre-release component
                    || r == '+')
                {
                    // Run completed (do not consume this char)
                    break;
                }

                value.Append(r);
            }

            if (value.Length > 0)
            {
                var s = value.ToString();
                if (type == SectionType.Numeric)
                {
                    s = s.TrimStart('0');
                }

                parsed.Add(new Section(type, s));
            }
        }

        this._sections = parsed.ToArray();
    }

    private static int CompareSections(Section a, Section b)
    {
        // If both components are numeric, compare numerically
        if (a.Type == SectionType.Numeric && b.Type == SectionType.Numeric)
        {
            if (a.Value.Length != b.Value.Length)
            {
                return a.Value.Length.CompareTo(b.Value.Length);
            }

            return string.CompareOrdinal(a.Value, b.Value);
        }

        // One or both are null
        if (a.Type == SectionType.Null)
        {
            return b.Type == SectionType.PreRelease ? 1 : -1;
        }

        if (b.Type == SectionType.Null)
        {
            return a.Type == SectionType.PreRelease ? -1 : 1;
        }

        // Textual comparison (differing type, or both textual/pre-release)
        var minLen = Math.Min(a.Value.Length, b.Value.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (a.Value[i] != b.Value[i])
            {
                return a.Value[i].CompareTo(b.Value[i]);
            }
        }

        return a.Value.Length.CompareTo(b.Value.Length);
    }

    public int CompareTo(McVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var size = Math.Max(_sections.Length, other._sections.Length);
        for (var i = 0; i < size; i++)
        {
            var sec1 = i < _sections.Length ? _sections[i] : default;
            var sec2 = i < other._sections.Length ? other._sections[i] : default;
            var cmp = CompareSections(sec1, sec2);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    public bool Equals(McVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as McVersion);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var section in _sections)
        {
            hash.Add((int)section.Type);
            hash.Add(section.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator <(McVersion left, McVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(McVersion left, McVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(McVersion left, McVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(McVersion left, McVersion right) => left.CompareTo(right) >= 0;

    public static bool operator ==(McVersion? left, McVersion? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return left.Equals(right);
    }

    public static bool operator !=(McVersion? left, McVersion? right) => !(left == right);
}
