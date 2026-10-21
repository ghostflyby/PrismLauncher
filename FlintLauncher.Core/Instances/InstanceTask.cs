// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/InstanceTask.{h,cpp}: the base class of instance *creation* tasks
// (copy / import / vanilla creation). A task runs inside a staging folder that the
// InstanceList hands out and commits once the task succeeds (InstanceList.WrapInstanceTask).
//
// Dropped (Qt GUI):
//  - the ask* free functions (askForChangingInstanceName/askIfShouldUpdate/
//    askIfShouldDeleteSaves) are message-box dialogs; only their enums
//    (InstanceNameChange/ShouldUpdate/ShouldDeleteSaves) are ported. Callers run the
//    dialog themselves and hand the outcome to <see cref="ShouldDeleteSaves"/>.
//  - downloadFiles(): drives createUpdateTask() (the P5 launch pipeline) through a
//    SequentialTask with a CustomMessageBox error dialog on failure — TODO(P5).
//  - the m_gameFilesTask abort plumbing belongs to downloadFiles.
//
// Deviations:
//  - scheduleToDelete() consults the settable <see cref="ShouldDeleteSaves"/> property
//    instead of opening a dialog when it is NotAsked (NotAsked and No both skip scheduling
//    "saves/" paths, Yes schedules them like the dialog's answer).
//  - the copy executes synchronously inside StartAsync (no QtConcurrent thread hop).

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Tasks;

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

public abstract class InstanceTask : FlintTask
{
    private readonly List<string> _filesToRemove = [];

    private string _originalName = "";
    private string _originalVersion = "";
    private string _modifiedName = "";

    private bool _overrideExisting;
    private bool _confirmUpdate = true;
    private string _originalInstanceId = "";

    /// <summary>The typed instance-layer environment (D4): user flint.json store + the
    /// instance.json/instance-state.json type infos. Set through
    /// <see cref="SetParentEnvironment"/> (the InstanceStaging wrapper does it before the
    /// task runs); the creation tasks build their staged instances with it.</summary>
    protected InstanceEnvironment? Environment { get; private set; }

    /// <summary>C++ m_instIcon + setIcon().</summary>
    public string Icon { get; set; } = "";

    /// <summary>C++ m_instGroup + setGroup()/group().</summary>
    public string Group { get; set; } = "";

    /// <summary>C++ m_targetDir + setTargetDir()/targetDir().</summary>
    public string TargetDir { get; set; } = "";

    /// <summary>C++ m_stagingPath + setStagingPath().</summary>
    public string StagingPath { get; set; } = "";

    /// <summary>C++ m_shouldDeleteSaves (upstream: the answer of a GUI dialog, asked on
    /// demand from scheduleToDelete()).</summary>
    public ShouldDeleteSaves ShouldDeleteSaves { get; set; } = ShouldDeleteSaves.NotAsked;

    /// <summary>C++ m_filesToRemove (consumed by the import/update flows).</summary>
    protected IReadOnlyList<string> FilesToRemove => _filesToRemove;

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
        this._overrideExisting = overrideExisting;
        if (instanceIdToOverride.Length != 0)
        {
            _originalInstanceId = instanceIdToOverride;
        }
    }

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

        _filesToRemove.Add(Path.Combine(dir, path));
        if (checkDisabled)
        {
            if (path.EndsWith(".disabled", StringComparison.Ordinal))
            {
                // remove the twin without the suffix (it was enabled/disabled by the user)
                _filesToRemove.Add(Path.Combine(dir, path[..^9]));
            }
            else
            {
                _filesToRemove.Add(Path.Combine(dir, path + ".disabled"));
            }
        }
    }
}
