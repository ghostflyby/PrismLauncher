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
// Port of launcher/meta/Index.{h,cpp} (the non-Qt subset). The QAbstractListModel roles
// (data()/rowCount()/headerData()), the Qt dataChanged wiring of connectVersionList() and the
// blocking getLoadedVersion() QEventLoop are UI plumbing and are not ported.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Meta;

public sealed class Index : MetaEntity
{
    private readonly List<VersionList> _lists = [];
    private readonly Dictionary<string, VersionList> _uids = new(StringComparer.Ordinal);

    public Index() { }

    public Index(IReadOnlyList<VersionList> lists)
    {
        _lists.AddRange(lists);
        foreach (var t in _lists)
        {
            _uids[t.Uid] = t;
        }
    }

    public override string LocalFilename => "index.json";

    // ------------------------------------------------------------------ queries

    public bool HasUid(string uid) => _uids.ContainsKey(uid);

    public VersionList Get(string uid)
    {
        if (!_uids.TryGetValue(uid, out var outList))
        {
            outList = new VersionList(uid);
            if (Cache is { } cache)
            {
                outList.Cache = cache;
            }

            _uids[uid] = outList;
            _lists.Add(outList);
        }

        return outList;
    }

    public MetaVersion Get(string uid, string version)
    {
        var list = Get(uid);
        return list.GetVersion(version);
    }

    public IReadOnlyList<VersionList> Lists() => _lists;

    // ------------------------------------------------------------------ for parsers

    public void Merge(Index other)
    {
        var lists = other._lists;
        // initial load, no need to merge
        if (_lists.Count == 0)
        {
            _lists.Clear();
            _lists.AddRange(lists);
            foreach (var list in lists)
            {
                _uids[list.Uid] = list;
            }
        }
        else
        {
            foreach (var list in lists)
            {
                if (Cache is { } cache)
                {
                    list.Cache = cache;
                }

                if (_uids.TryGetValue(list.Uid, out var existing))
                {
                    existing.MergeFromIndex(list);
                }
                else
                {
                    _lists.Add(list);
                    _uids[list.Uid] = list;
                }
            }
        }
    }

    public override void Parse(JsonObject obj)
    {
        MetaJsonFormat.ParseIndex(obj, this);
    }

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Port of Index::loadVersion(): chains the index, version-list and version load tasks
    /// (offline mode or plain lookups short-circuit to just the version load task). The
    /// "MetaRefreshOnLaunch" setting gate is not ported because the settings key does not
    /// exist yet; online loads always attempt a refresh like force=true, minus the tasks that
    /// are already loaded (loadTask() emits immediately for fresh local files).
    /// </summary>
    public FlintTask LoadVersion(string uid, string version, NetMode mode = NetMode.Online, bool force = false)
    {
        if (mode == NetMode.Offline)
        {
            return Get(uid, version).LoadTask(mode);
        }

        var versionList = Get(uid);
        var loadTask = new SequentialTask($"Load meta for {uid}:{version}");
        if (Status() != LoadStatus.Remote || force)
        {
            loadTask.AddTask(LoadTask(mode));
        }

        loadTask.AddTask(versionList.LoadTask(mode));
        loadTask.AddTask(versionList.GetVersion(version).LoadTask(mode));
        return loadTask;
    }
}
