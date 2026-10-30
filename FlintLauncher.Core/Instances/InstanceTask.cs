// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// The base class of instance *creation* tasks (copy / import / vanilla creation). A task runs
// inside a staging folder that the InstanceList hands out and commits once the task returns
// (InstanceList.WrapInstanceTask); a task that throws leaves its staging folder removed.
//
// Dropped (Qt GUI):
//  - the ask* free functions (askForChangingInstanceName/askIfShouldUpdate/
//    askIfShouldDeleteSaves) are message-box dialogs; only their enums
//    (InstanceNameChange/ShouldUpdate/ShouldDeleteSaves) are ported. Callers run the
//    dialog themselves and hand the outcome to <see cref="ShouldDeleteSaves"/>.
//  - downloadFiles(): drives createUpdateTask() (the launch pipeline) through the C++
//    SequentialTask with a CustomMessageBox error dialog on failure — TODO.
//  - the m_gameFilesTask abort plumbing belongs to downloadFiles.
//
// Deviations:
//  - scheduleToDelete() consults the settable <see cref="ShouldDeleteSaves"/> property
//    instead of opening a dialog when it is NotAsked (NotAsked and No both skip scheduling
//    "saves/" paths, Yes schedules them like the dialog's answer).
//  - the copy runs inline in the task body (no QtConcurrent thread hop).

using FlintLauncher.Core.Config;
using TruePath;

namespace FlintLauncher.Core.Instances;

/// <summary>Port of the InstanceNameChange enum (UI dialog outcome).</summary>
public enum InstanceNameChange : byte
{
    ShouldChange,
    ShouldKeep,
}

/// <summary>Port of the ShouldUpdate enum (UI dialog outcome).</summary>
public enum ShouldUpdate : byte
{
    Update,
    SkipUpdating,
    Cancel,
}

/// <summary>Port of the ShouldDeleteSaves enum (UI dialog outcome).</summary>
public enum ShouldDeleteSaves : byte
{
    NotAsked,
    Yes,
    No,
}

public abstract class InstanceTask
{
    private readonly List<AbsolutePath> _filesToRemove = [];

    private string _originalName = "";
    private string _originalVersion = "";
    private string _modifiedName = "";

    private bool _overrideExisting;
    private bool _confirmUpdate = true;
    private string _originalInstanceId = "";

    /// <summary>The typed instance-layer environment: user flint.json store + the
    /// instance.json/instance-state.json type infos. Set through
    /// <see cref="SetParentEnvironment"/> (the InstanceStaging wrapper does it before the
    /// task runs); the creation tasks build their staged instances with it.</summary>
    protected InstanceEnvironment? Environment { get; private set; }

    /// <summary>C++ m_instIcon + setIcon().</summary>
    public string Icon { get; set; } = "";

    /// <summary>C++ m_instGroup + setGroup()/group().</summary>
    public string Group { get; set; } = "";

    /// <summary>C++ m_targetDir + setTargetDir()/targetDir().</summary>
    public AbsolutePath TargetDir { get; init; }

    public AbsolutePath StagingPath { get; set; }

    /// <summary>C++ m_shouldDeleteSaves (upstream: the answer of a GUI dialog, asked on
    /// demand from scheduleToDelete()).</summary>
    public ShouldDeleteSaves ShouldDeleteSaves { get; set; } = ShouldDeleteSaves.NotAsked;

    /// <summary>C++ m_filesToRemove (consumed by the import/update flows).</summary>
    protected IReadOnlyList<AbsolutePath> FilesToRemove => _filesToRemove;

    public void SetParentEnvironment(InstanceEnvironment environment) => Environment = environment;

    public bool ShouldConfirmUpdate() => _confirmUpdate;

    public void SetConfirmUpdate(bool confirm) => _confirmUpdate = confirm;

    public bool ShouldOverride => _overrideExisting;

    /// <summary>C++ originalInstanceID().</summary>
    public string OriginalInstanceId => _originalInstanceId;

    public string OriginalName() => _originalName;

    public string ModifiedName() => _modifiedName.Length != 0 ? _modifiedName : _originalName;

    /// <summary>C++ name(): the display name the created instance will get.</summary>
    public string Name()
    {
        if (_modifiedName.Length != 0)
        {
            return ModifiedName();
        }

        if (_originalVersion.Length != 0)
        {
            return _originalName + " " + _originalVersion;
        }

        return _originalName;
    }

    /// <summary>C++ version().</summary>
    public string Version() => _originalVersion;

    public void SetName(string name) => _modifiedName = name;

    public void SetOriginalName(string name, string version)
    {
        _originalName = name;
        _originalVersion = version;
    }

    public void SetOverride(bool overrideExisting, string instanceIdToOverride = "")
    {
        _overrideExisting = overrideExisting;
        if (instanceIdToOverride.Length != 0)
        {
            _originalInstanceId = instanceIdToOverride;
        }
    }

    /// <summary>Runs the creation. Returning succeeds it; throwing fails it with the
    /// exception's message as the reason. The work happens inside <see cref="StagingPath"/>,
    /// which the staging wrapper commits once this returns.</summary>
    internal abstract Task ExecuteAsync(CancellationToken cancellationToken);

    /// <summary>Narration for the task log: what the creation is doing right now. It is not
    /// task state — the outcome is the run's.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>How far the creation has got. The window it feeds is the host's.</summary>
    public event Action<long, long>? ProgressChanged;

    protected void EmitStatus(string status) => StatusChanged?.Invoke(status);

    protected void SetProgress(long current, long total) => ProgressChanged?.Invoke(current, total);

    /// <summary>
    /// Port of scheduleToDelete(): schedules a relative path (inside <paramref name="dir"/>)
    /// for removal once the creation/update flow finishes. "saves/" paths are only removed
    /// when <see cref="ShouldDeleteSaves"/> is Yes.
    /// </summary>
    public void ScheduleToDelete(string dir, string path, bool checkDisabled = false)
    {
        if (path.Length == 0)
        {
            return;
        }

        if (path.StartsWith("saves/", StringComparison.Ordinal))
        {
            if (ShouldDeleteSaves == ShouldDeleteSaves.NotAsked)
            {
                // (upstream opens the askIfShouldDeleteSaves() dialog here)
                ShouldDeleteSaves = ShouldDeleteSaves.No;
            }

            if (ShouldDeleteSaves == ShouldDeleteSaves.No)
            {
                return;
            }
        }

        _filesToRemove.Add(new AbsolutePath(dir) / path);
        if (checkDisabled)
        {
            if (path.EndsWith(".disabled", StringComparison.Ordinal))
            {
                // remove the twin without the suffix (it was enabled/disabled by the user)
                string a1 = path[..^9];
                _filesToRemove.Add(new AbsolutePath(dir) / a1);
            }
            else
            {
                string a1 = path + ".disabled";
                _filesToRemove.Add(new AbsolutePath(dir) / a1);
            }
        }
    }
}
