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
// Port of launcher/NullInstance.h: the placeholder instance used when an instance folder
// exists but its type cannot be instantiated.

using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Settings;
using TruePath;

namespace FlintLauncher.Core.Instances;

public class NullInstance : BaseInstance
{
    public NullInstance(SettingsObject settings, AbsolutePath rootDir)
        : base(settings, rootDir)
    {
        SetVersionBroken(true);
    }

    public override void SaveNow() { }

    public override void LoadSpecificSettings() => SetSpecificSettingsLoaded(true);

    public override string GetStatusbarDescription() => "Unknown instance type";

    public override IReadOnlySet<string> Traits() => new HashSet<string>();

    public override AbsolutePath InstanceConfigFolder() => InstanceRoot;

    public override LaunchTask? CreateLaunchTask(AuthSession? account, MinecraftTarget? targetToJoin) => null;

    public override List<Func<CancellationToken, Task>> CreateUpdateTask() => [];

    public override SortedDictionary<string, string> CreateEnvironment() =>
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public override SortedDictionary<string, string> CreateLaunchEnvironment() =>
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public override SortedDictionary<string, string> GetVariables() => [];

    public override IReadOnlyList<AbsolutePath> GetLogFileSearchPaths() => [];

    public override bool CanExport() => false;

    public override bool CanEdit() => false;

    public override bool CanLaunch() => false;

    public override List<string> VerboseDescription(AuthSession? session, MinecraftTarget? targetToJoin)
    {
        return ["Null instance - placeholder."];
    }

    public override AbsolutePath ModsRoot => new("");

    public override void UpdateRuntimeContext()
    {
        // NOOP
    }
}
