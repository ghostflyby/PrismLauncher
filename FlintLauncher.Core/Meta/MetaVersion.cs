// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2015-2021 MultiMC Contributors
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
// Port of launcher/meta/Version.{h,cpp}: a metadata version record wrapping a VersionFile.
// The QObject signals (typeChanged/timeChanged/requiresChanged) are model plumbing for the Qt
// views and are not ported; MergeFromList assigns directly like upstream.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Meta;

public sealed class MetaVersion(string uid, string version) : MetaEntity
{
    public bool ProvidesRecommendations { get; set; }

    public SortedSet<Require> Conflicts { get; set; } = [];

    public bool Volatile { get; set; }

    /// <summary>Port of BaseVersion::name().</summary>
    public string Name => Data is not null ? Data.Name : Uid;

    public string Uid { get; } = uid;

    public string Version { get; } = version;

    public string? Type { get; set; }

    /// <summary>QDateTime::fromMSecsSinceEpoch(m_time * 1000, Qt::UTC).</summary>
    public DateTimeOffset Time() => DateTimeOffset.FromUnixTimeMilliseconds(RawTime * 1000);

    public long RawTime { get; set; }

    public SortedSet<Require> RequiredSet { get; set; } = [];

    public VersionFile? Data { get; set; }

    public bool IsRecommended { get; set; }

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
        if (other.ProvidesRecommendations)
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

        if (!Conflicts.SetEquals(other.Conflicts))
        {
            Conflicts = [.. other.Conflicts];
        }

        if (Volatile != other.Volatile)
        {
            Volatile = other.Volatile;
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
}
