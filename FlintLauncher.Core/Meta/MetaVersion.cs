// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/Version.{h,cpp}: a metadata version record wrapping a VersionFile.
// The QObject signals (typeChanged/timeChanged/requiresChanged) are model plumbing for the Qt
// views and are not ported; MergeFromList assigns directly like upstream.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Meta;

public sealed class MetaVersion(string uid, string version) : MetaEntity
{
    private bool providesRecommendations;

    // NOTE: the C++ m_name member is dead there as well (never written); dropped to keep
    // the port warning-clean

    private SortedSet<Require> conflicts = new();

    private bool @volatile;

    /// <summary>Port of BaseVersion::name().</summary>
    public string Name => Data is not null ? Data.Name : Uid;

    public string Uid { get; } = uid;

    public string Version { get; } = version;

    public string Type { get; set; } = "";

    /// <summary>QDateTime::fromMSecsSinceEpoch(m_time * 1000, Qt::UTC).</summary>
    public DateTimeOffset Time() => DateTimeOffset.FromUnixTimeMilliseconds(RawTime * 1000);

    public long RawTime { get; set; }

    public SortedSet<Require> RequiredSet { get; private set; } = [];

    public VersionFile? Data { get; set; }

    public bool IsRecommended { get; set;  }

    public override bool IsLoaded => Data is not null && base.IsLoaded;

    public void Merge(MetaVersion other)
    {
        MergeFromList(other);
        if (other.Data is not null)
        {
            Data = other.Data;
        }
    }

    public void MergeFromList(MetaVersion other)
    {
        if (other.providesRecommendations)
        {
            if (IsRecommended != other.IsRecommended)
            {
                IsRecommended = other.IsRecommended;
            }
        }

        if (Type != other.Type)
        {
            Type = other.Type;
        }

        if (RawTime != other.RawTime)
        {
            RawTime = other.RawTime;
        }

        if (!RequiredSet.SetEquals(other.RequiredSet))
        {
            RequiredSet = [.. other.RequiredSet];
        }

        if (!conflicts.SetEquals(other.conflicts))
        {
            conflicts = [.. other.conflicts];
        }

        if (@volatile != other.@volatile)
        {
            SetVolatile(other.@volatile);
        }

        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }
    }

    public override void Parse(JsonObject obj)
    {
        MetaJsonFormat.ParseVersion(obj, this);
    }

    public override string LocalFilename => Uid + '/' + Version + ".json";

    /// <summary>Port of toComparableVersion(): a FlexVer comparable version of the descriptor.</summary>
    public McVersion ToComparableVersion()
    {
        return new McVersion(Version);
    }

    // ------------------------------------------------- for usage by format parsers only

    public void SetRequires(SortedSet<Require> reqs, SortedSet<Require> conflicts)
    {
        RequiredSet = reqs;
        this.conflicts = conflicts;
    }

    public void SetVolatile(bool volatileValue) => @volatile = volatileValue;

    public void SetProvidesRecommendations() => providesRecommendations = true;
}
