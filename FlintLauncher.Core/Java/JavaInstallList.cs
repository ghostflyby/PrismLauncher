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

using System.Collections;
using JdkFind;
using TruePath;

namespace FlintLauncher.Core.Java;

public sealed class JavaInstallList : IReadOnlyList<JavaExecutable>
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
    private List<JavaExecutable> _vlist = [];

    /// <summary><paramref name="javaRoot"/> is the launcher-managed java dir (the JavaDir
    /// setting under store semantics — the host passes the resolved root; null means there is
    /// no launcher-managed root to scan).</summary>
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

    public bool IsLoaded => _status == Status.Done;

    public int Count => _vlist.Count;

    public JavaExecutable this[int index] => _vlist[index];

    public IReadOnlyList<JavaExecutable> Versions => _vlist;

    public IEnumerator<JavaExecutable> GetEnumerator() => _vlist.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Port of JavaInstallList::updateListData().</summary>
    public void UpdateListData(IReadOnlyList<JavaExecutable> versions)
    {
        _vlist = [.. versions];
        SortVersions();
        _status = Status.Done;
    }

    /// <summary>
    /// Port of JavaInstallList::sortVersions() with the sortJavas predicate: an ASCENDING order
    /// by architecture first, then version id, then path.
    /// </summary>
    public void SortVersions()
    {
        _vlist.Sort(JavaExecutableComparer.Instance);
    }
}

/// <summary>Port of JavaListLoadTask. The detection itself lives in
/// <see cref="JavaDetection"/> (JdkFind LocateExecutables); this is the list-model shell
/// around it.</summary>
public static class JavaListLoadTask
{
    /// <summary>Fills the list from the detected executables that ran successfully —
    /// the upstream list only carries valid installations. Returning is success; any other
    /// exception is a failure whose message is the reason.</summary>
    public static async Task RunAsync(JavaInstallList vlist, CancellationToken cancellationToken,
        bool onlyManagedVersions = false, AbsolutePath? javaRoot = null,
        IReadOnlyList<IJvmProvider>? providers = null)
    {
        var executables = await JavaDetection.DetectAsync(javaRoot, cancellationToken,
            providers: providers, managedOnly: onlyManagedVersions).ConfigureAwait(false);

        vlist.UpdateListData(executables.Where(executable => executable.StartFailure is null).ToList());
    }
}
