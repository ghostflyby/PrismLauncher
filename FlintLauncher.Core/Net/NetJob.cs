// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
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
// Port of launcher/net/NetJob.{h,cpp}: a ConcurrentTask of download tasks over the shared
// persistent client.

using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Net;

public class NetJob(string name = "", int maxConcurrent = ConcurrentTask.DefaultMaxConcurrent) : ConcurrentTask(name, maxConcurrent)
{
    public void AddDownload(HttpDownloadTask download) => AddTask(download);

    public IReadOnlyList<HttpDownloadTask> Downloads => Queue.OfType<HttpDownloadTask>()
        .Concat(Done.Keys.OfType<HttpDownloadTask>()).ToList();
}
