// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
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
// Port of launcher/java/JavaInstallList.{h,cpp}: a list of discovered JavaInstall entries plus
// the JavaListLoadTask that fills it. Detection lives in JavaDetection (JdkFind LocateAsync
// primary + FromExecutable fallback); this task maps the candidates onto JavaInstall entries.
//
// Deviations from the C++ original:
//  - The BaseVersionList/QAbstractListModel surface (data() roles, VersionPointerRole, ...)
//    is UI plumbing and is not ported. RecommendedRole upstream is a constant `false` in this
//    revision (m_current_recommended is initialised to NULL and never assigned), so there is
//    no recommendation logic to port; consumers that need a default pick the newest entry.
//  - Detection no longer runs JavaCheck.jar per candidate (see JavaDetection); the list
//    metadata comes from the release file + runtime probe instead of the checker output.
//  - The APPLICATION->settings()->get("NumberOfConcurrentTasks") lookup died with the
//    JavaCheck fan-out: the JdkFind pipeline manages its own parallelism.

using JdkFind;
using TruePath;

namespace FlintLauncher.Core.Java;

public sealed class JavaInstallList
{
    private enum Status
    {
        NotDone,
        InProgress,
        Done,
    }

    private readonly bool _onlyManagedVersions;
    private readonly AbsolutePath? _javaRoot;
    private Status _status = Status.NotDone;
    private Task? _loadTask;
    private List<JavaInstall> _vlist = [];

    /// <summary><paramref name="javaRoot"/> is the launcher-managed java dir (the JavaDir
    /// setting under store semantics — the host passes the resolved root; the default
    /// keeps the C++ cwd-relative behavior for bare fixtures).</summary>
    public JavaInstallList(bool onlyManagedVersions = false, AbsolutePath? javaRoot = null)
    {
        _onlyManagedVersions = onlyManagedVersions;
        _javaRoot = javaRoot;
    }

    /// <summary>Port of JavaInstallList::getLoadTask(): the in-flight load, or a fresh one
    /// already started. A failure leaves the list as it was.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loadTask is { IsCompleted: false } inFlight)
        {
            return inFlight;
        }

        _status = Status.InProgress;
        _loadTask = JavaListLoadTask.RunAsync(this, cancellationToken, _onlyManagedVersions, _javaRoot);
        return _loadTask;
    }

    public bool IsLoaded() => _status == Status.Done;

    public JavaInstall At(int i) => _vlist[i];

    public int Count => _vlist.Count;

    public IReadOnlyList<JavaInstall> Versions() => _vlist;

    /// <summary>Port of JavaInstallList::updateListData().</summary>
    public void UpdateListData(IReadOnlyList<JavaInstall> versions)
    {
        _vlist = [.. versions];
        SortVersions();
        _status = Status.Done;
    }

    /// <summary>
    /// Port of JavaInstallList::sortVersions() with the sortJavas predicate: an ASCENDING order
    /// by JavaInstall::CompareTo (architecture first, then version id, then path).
    /// </summary>
    public void SortVersions()
    {
        _vlist.Sort();
    }
}

/// <summary>Port of JavaListLoadTask. The detection itself lives in
/// <see cref="JavaDetection"/> (JdkFind LocateAsync primary + FromExecutable fallback); this
/// is the list-model shell around it and maps the candidates onto
/// <see cref="JavaInstall"/>.</summary>
public static class JavaListLoadTask
{
    /// <summary>Fills the list from the detected installations. Returning is success; any
    /// other exception is a failure whose message is the reason.</summary>
    public static async Task RunAsync(JavaInstallList vlist, CancellationToken cancellationToken,
        bool onlyManagedVersions = false, AbsolutePath? javaRoot = null,
        IReadOnlyList<IJvmProvider>? providers = null)
    {
        var candidates = await JavaDetection.DetectAsync(javaRoot, cancellationToken,
            providers: providers, managedOnly: onlyManagedVersions).ConfigureAwait(false);

        var installs = candidates
            .Where(candidate => candidate.Valid)
            .Select(candidate => new JavaInstall(candidate.Version, candidate.Architecture, candidate.Path)
            {
                LanguageVersion = candidate.LanguageVersion,
                Is64Bit = JavaUtils.Is64BitArchitecture(candidate.Architecture),
            })
            .ToList();

        vlist.UpdateListData(installs);
    }
}
