// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The value a work unit reports progress with: how far along, out of how much. A unit that
// wants to report takes an IProgress<TaskProgress>; null runs it silent.

namespace FlintLauncher.Core.Tasks;

/// <summary>One progress report: <paramref name="Current"/> out of <paramref name="Total"/>.</summary>
public readonly record struct TaskProgress(long Current, long Total);
