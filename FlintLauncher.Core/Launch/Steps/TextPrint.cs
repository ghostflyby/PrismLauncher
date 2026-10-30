// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
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
// Prints static text into the launch log.

namespace FlintLauncher.Core.Launch.Steps;

public sealed class TextPrint : AwaitedStep
{
    private readonly List<string> _lines;
    private readonly MessageLevel _level;

    public TextPrint(LaunchTask parent, IReadOnlyList<string> lines, MessageLevel level) : base(parent)
    {
        _lines = [.. lines];
        _level = level;
    }

    public TextPrint(LaunchTask parent, string line, MessageLevel level) : base(parent)
    {
        _lines = [line];
        _level = level;
    }

    protected override Task RunAsync(CancellationToken cancellationToken)
    {
        EmitLogLines(_lines, _level);
        return Task.CompletedTask;
    }

    public override bool CanAbort() => true;

    public override bool AbortStep() => true;
}
