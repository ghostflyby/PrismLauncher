// SPDX-License-Identifier: GPL-3.0-only
// Tests for the Java::Metadata and JavaInstall comparators and the metadata parser. The C++
// tests directory only carries JavaVersion_test.cpp (no JavaMetadata/JavaInstall comparator
// tests), so these are table checks written against the upstream operator semantics.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Java;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaMetadataSortTests
{
    private static JavaMetadata Meta(string name, string version, string releaseTime, string vendor = "vendor",
        string downloadType = "archive", string runtimeOs = "osx")
    {
        // mirror the meta format: the numeric fields are parsed out of the dotted version
        var parts = version.Split('.');
        var major = int.Parse(parts[0]);
        var minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        var security = parts.Length > 2 ? int.Parse(parts[2]) : 0;
        return JavaMetadata.ParseJavaMeta(new JsonObject
        {
            ["name"] = name,
            ["vendor"] = vendor,
            ["url"] = $"https://example.invalid/{name}.tar.gz",
            ["releaseTime"] = releaseTime,
            ["downloadType"] = downloadType,
            ["packageType"] = "tar.gz",
            ["runtimeOS"] = runtimeOs,
            ["checksum"] = new JsonObject
            {
                ["type"] = "sha1",
                ["hash"] = "0123456789012345678901234567890123456789",
            },
            ["version"] = new JsonObject
            {
                ["name"] = version,
                ["major"] = major,
                ["minor"] = minor,
                ["security"] = security,
                ["build"] = 0,
            },
        });
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void ParseJavaMeta_FillsAllFields()
    {
        var meta = Meta("java-macos", "17.0.9", "2023-10-17T12:00:00+00:00", vendor: "adoptium");

        Assert.Equal("java-macos", meta.Name());
        Assert.Equal("adoptium", meta.Vendor);
        Assert.Equal("https://example.invalid/java-macos.tar.gz", meta.Url);
        Assert.NotNull(meta.ReleaseTime);
        Assert.Equal(JavaDownloadType.Archive, meta.DownloadType);
        Assert.Equal("tar.gz", meta.PackageType);
        Assert.Equal("osx", meta.RuntimeOs);
        Assert.Equal("sha1", meta.ChecksumType);
        Assert.Equal("0123456789012345678901234567890123456789", meta.ChecksumHash);
        Assert.Equal("17.0.9", meta.Version.ToString());
        Assert.Equal("17.0.9", meta.Descriptor());
        Assert.Equal("adoptium", meta.TypeString());
    }

    [Fact]
    public void ParseJavaMeta_DefaultsAndUnknownDownloadType()
    {
        var meta = JavaMetadata.ParseJavaMeta(new JsonObject
        {
            ["name"] = "m",
            ["downloadType"] = "carrier-pigeon",
        });

        Assert.Equal(JavaDownloadType.Unknown, meta.DownloadType);
        Assert.Equal("unknown", meta.RuntimeOs);
        Assert.Equal("", meta.Vendor);
        Assert.Null(meta.ReleaseTime);
        Assert.Equal(new JavaVersion().ToString(), meta.Version.ToString());
    }

    [Fact]
    public void ParseJavaMeta_VersionSubFieldsAreOptional()
    {
        // #6143/#6159: the meta "version" object's members may be missing; they fall back to
        // the QJsonValue defaults ("", 0) instead of failing the whole parse
        var meta = JavaMetadata.ParseJavaMeta(new JsonObject
        {
            ["name"] = "java",
            ["version"] = new JsonObject(),
        });
        Assert.Equal("0", meta.Version.ToString()); // all-numeric defaults (major=0)
        Assert.Equal("java", meta.Name());

        var partial = JavaMetadata.ParseJavaMeta(new JsonObject
        {
            ["name"] = "java",
            ["version"] = new JsonObject { ["major"] = 17 },
        });
        Assert.Equal("17", partial.Version.ToString());
    }

    [Theory]
    [InlineData("manifest", JavaDownloadType.Manifest)]
    [InlineData("archive", JavaDownloadType.Archive)]
    [InlineData("nope", JavaDownloadType.Unknown)]
    [InlineData("", JavaDownloadType.Unknown)]
    public void DownloadType_RoundTrips(string input, JavaDownloadType type)
    {
        Assert.Equal(type, JavaMetadata.ParseDownloadType(input));
        Assert.Equal(input.Length == 0 || input == "nope" ? "unknown" : input,
            JavaMetadata.DownloadTypeToString(type));
    }

    // -------------------------------------------------------------- comparators

    [Theory]
    [InlineData("17.0.8", "17.0.9", true)] // version decides
    [InlineData("17.0.9", "17.0.8", false)]
    [InlineData("17.0.9", "17.0.9", false)] // equal versions fall through to release time
    public void LessThan_ByVersion(string lhsVersion, string rhsVersion, bool lhsSmaller)
    {
        // same release time on both sides: the name becomes the tiebreak, so keep names equal
        var lhs = Meta("same-name", lhsVersion, "2023-01-01T00:00:00+00:00");
        var rhs = Meta("same-name", rhsVersion, "2023-01-01T00:00:00+00:00");

        Assert.Equal(lhsSmaller, lhs.LessThan(rhs));
        Assert.Equal(!lhsSmaller && lhsVersion != rhsVersion, lhs.GreaterThan(rhs));
        Assert.Equal(lhsVersion == rhsVersion, lhs == rhs);
    }

    [Fact]
    public void LessThan_ReleaseTimeBreaksVersionTies()
    {
        var older = Meta("same-name", "17.0.9", "2022-01-01T00:00:00+00:00");
        var newer = Meta("same-name", "17.0.9", "2023-01-01T00:00:00+00:00");

        Assert.True(older < newer);
        Assert.False(newer < older);
        // NOTE upstream: operator== only compares version + name, so equal versions with
        // different release times still compare equal
        Assert.True(older == newer);
    }

    [Fact]
    public void LessThan_NameBreaksTimeTies_NaturallyAndCaseInsensitive()
    {
        var alpha = Meta("java-beta10", "17.0.9", "2023-01-01T00:00:00+00:00");
        var beta = Meta("java-beta9", "17.0.9", "2023-01-01T00:00:00+00:00");
        // natural order: 9 < 10
        Assert.True(beta < alpha);

        var upper = Meta("JAVA", "17.0.9", "2023-01-01T00:00:00+00:00");
        var lower = Meta("java", "17.0.9", "2023-01-01T00:00:00+00:00");
        // case-insensitive natural compare yields equality on the name tiebreak
        Assert.False(upper < lower);
        Assert.False(lower < upper);
    }

    [Fact]
    public void InvalidReleaseTime_IsSmallerThanValidOne()
    {
        // Qt compares an invalid QDateTime as smaller than any valid one
        var invalid = Meta("same-name", "17.0.9", "not-a-date");
        var valid = Meta("same-name", "17.0.9", "2023-01-01T00:00:00+00:00");

        Assert.True(invalid < valid);
        Assert.False(valid < invalid);
    }

    // ------------------------------------------------------- JavaInstall comparators

    [Fact]
    public void JavaInstall_SortsByArchThenVersionThenPath()
    {
        var x64New = new JavaInstall("17.0.9", "x86_64", "/a");
        var x64Old = new JavaInstall("11.0.2", "x86_64", "/b");
        var arm64 = new JavaInstall("21", "aarch64", "/c");

        // architecture compares first (natural, case-insensitive: aarch64 < x86_64)
        Assert.True(arm64 < x64New);
        Assert.True(x64Old < x64New);
        // equal arch + version: the path breaks the tie
        Assert.True(new JavaInstall("17", "x86_64", "/a") < new JavaInstall("17", "x86_64", "/b"));
        Assert.True(x64New == new JavaInstall("17.0.9", "x86_64", "/a"));
        Assert.True(x64New > x64Old);
    }

    [Fact]
    public void JavaInstallList_SortVersions_MatchesUpstreamPredicate()
    {
        var list = new JavaInstallList();
        var installs = new List<JavaInstall>
        {
            new("1.8.0_402", "x86_64", "/old"),
            new("21.0.3", "aarch64", "/zulu"),
            new("17.0.9", "x86_64", "/temurin"),
        };
        list.UpdateListData(installs);

        Assert.Equal(3, list.Count);
        Assert.True(list.IsLoaded());
        // JavaInstallList::sortVersions() sorts with sortJavas: comp(left, right) = right > left
        // (trichotomy: left < right) — an ASCENDING order by operator<. Architecture compares
        // first (aarch64 < x86_64), then the version id ascending, then the path.
        var versions = list.Versions();
        Assert.Equal("21.0.3", versions[0].Id.ToString());
        Assert.Equal("1.8.0_402", versions[1].Id.ToString());
        Assert.Equal("17.0.9", versions[2].Id.ToString());
    }
}
