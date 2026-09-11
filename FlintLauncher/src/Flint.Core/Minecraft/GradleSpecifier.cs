// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/GradleSpecifier.h: a maven-style coordinate
// "group:artifact:version[:classifier][@extension]".
//
// Ported as a readonly struct so C++ value-copy semantics (e.g. `GradleSpecifier
// nativeSpec = m_name; nativeSpec.setClassifier(...)`) are preserved; classification
// changes go through WithClassifier() which returns the modified copy.
using System.Text.RegularExpressions;

namespace Flint.Core.Minecraft;

public readonly partial struct GradleSpecifier : IEquatable<GradleSpecifier>
{
    /*
    org.gradle.test.classifiers : service : 1.0 : jdk15 @ jar
     0 "org.gradle.test.classifiers:service:1.0:jdk15@jar"
     1 "org.gradle.test.classifiers"
     2 "service"
     3 "1.0"
     4 "jdk15"
     5 "jar"
    */
    private static readonly Regex Matcher =
        new(@"\A(?:([^:@]+):([^:@]+):([^:@]+)(?::([^:@]+))?(?:@([^:@]+))?)\z",
            RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    public GradleSpecifier()
    {
        // default constructed: not valid, everything empty
    }

    public GradleSpecifier(string value)
    {
        var match = Matcher.Match(value ?? "");
        IsValid = match.Success;
        if (!IsValid)
        {
            InvalidValue = value ?? "";
            return;
        }
        GroupId = match.Groups[1].Value;
        ArtifactId = match.Groups[2].Value;
        Version = match.Groups[3].Value;
        Classifier = match.Groups[4].Success ? match.Groups[4].Value : "";
        // match.lastCapturedIndex() >= 5: the extension group participated
        if (match.Groups[5].Success)
        {
            Extension = match.Groups[5].Value;
        }
    }

    /// <summary>The raw unparsable input, returned by <see cref="Serialize"/> for invalid specifiers.</summary>
    public string InvalidValue { get; private init; } = "";

    public string GroupId { get; private init; } = "";

    public string ArtifactId { get; private init; } = "";

    public string Version { get; private init; } = "";

    public string Classifier { get; private init; } = "";

    /// <summary>Present only when the "@extension" part was given (std::optional&lt;QString&gt; upstream).</summary>
    public string? Extension { get; private init; }

    public bool IsValid { get; private init; }

    public GradleSpecifier WithClassifier(string classifier)
    {
        return new GradleSpecifier
        {
            IsValid = IsValid,
            InvalidValue = InvalidValue,
            GroupId = GroupId,
            ArtifactId = ArtifactId,
            Version = Version,
            Classifier = classifier,
            Extension = Extension,
        };
    }

    public readonly string Serialize()
    {
        if (!IsValid)
        {
            return InvalidValue;
        }
        var retval = GroupId + ":" + ArtifactId + ":" + Version;
        if (Classifier.Length != 0)
        {
            retval += ":" + Classifier;
        }
        if (Extension is not null)
        {
            retval += "@" + Extension;
        }
        return retval;
    }

    public readonly string? GetFileName()
    {
        if (!IsValid)
        {
            return null;
        }
        var filename = ArtifactId + '-' + Version;
        if (Classifier.Length != 0)
        {
            filename += "-" + Classifier;
        }
        filename += "." + (Extension ?? "jar");
        return filename;
    }

    /// <summary>Port of toPath(); returns null for invalid specifiers (QString() upstream).</summary>
    public readonly string? ToPath(string filenameOverride = "")
    {
        if (!IsValid)
        {
            return null;
        }
        var filename = filenameOverride.Length == 0 ? GetFileName()! : filenameOverride;
        var path = GroupId.Replace('.', '/');
        path += '/' + ArtifactId + '/' + Version + '/' + filename;
        return path;
    }

    /// <summary>returns the full group and artifact prefix</summary>
    public readonly string ArtifactPrefix() => GroupId + ":" + ArtifactId;

    public readonly bool MatchName(GradleSpecifier other)
    {
        return other.ArtifactId == ArtifactId && other.GroupId == GroupId && other.Classifier == Classifier;
    }

    // C++ operator== = default: compares every member, including validity state,
    // the invalid value and the optional extension.
    public readonly bool Equals(GradleSpecifier other)
    {
        return IsValid == other.IsValid
            && InvalidValue == other.InvalidValue
            && GroupId == other.GroupId
            && ArtifactId == other.ArtifactId
            && Version == other.Version
            && Classifier == other.Classifier
            && Extension == other.Extension;
    }

    public override readonly bool Equals(object? obj) => obj is GradleSpecifier other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsValid);
        hash.Add(InvalidValue, StringComparer.Ordinal);
        hash.Add(GroupId, StringComparer.Ordinal);
        hash.Add(ArtifactId, StringComparer.Ordinal);
        hash.Add(Version, StringComparer.Ordinal);
        hash.Add(Classifier, StringComparer.Ordinal);
        hash.Add(Extension, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(GradleSpecifier left, GradleSpecifier right) => left.Equals(right);

    public static bool operator !=(GradleSpecifier left, GradleSpecifier right) => !left.Equals(right);

    public override readonly string ToString() => Serialize();
}
