// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022 Jamie Mansfield <jmansfield@cadixdev.org>
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
// Port of launcher/BaseInstance.{h,cpp} including the getConsoleMaxLines/
// shouldStopOnConsoleOverflow free functions and the ShortcutData representation.
//
// Deviations from the C++ original:
//  - QObject signals become BCL notification contracts: propertiesChanged ->
//    INotifyPropertyChanged (view-property names: Name/IconKey/Notes/IsRunning/…; an empty
//    name is the whole-instance-view notification), runningStatusChanged ->
//    RunningStatusChanged, statusChanged -> StatusChanged. The
//    launchTaskChanged/profilerChanged signals belong to the launch pipeline.
//  - setMinecraftRunning() adds the played seconds to the launcher runtime state's
//    Playtime.Total (launcher-state.json; the C++ used the "TotalPlayTime" settings
//    key — moved out of the config world). The state store is injected
//    through <see cref="LauncherState"/>; when it is null the global accumulation is
//    skipped (all instance-local keys behave as upstream).
//  - iconUpdated() received the icon list singleton's signal; it becomes a plain method
//    taking the updated key.
//  - populateLaunchMenu() (QMenu construction) is pure UI and is not ported.
//  - The qWarning for shortcuts with non-existent paths is dropped (no logging layer at
//    this level); the shortcuts are skipped like upstream.
//  - QUuid::Id128 form maps to Guid.ToString("N") (32 hex digits, no separators).
//  - instance.json: the settings object IS the typed layer stack —
//    flint.json (user) -> instance.json (instance) — see
//    Core.Config.InstanceJsonSettingsObject. The C++ override-gate machinery
//    (RegisterOverride/RegisterPassthrough, OverrideSetting/PassthroughSetting) is retired:
//    the gates degenerate to "instance-layer member is non-null", so inheritance comes
//    from the fold and the per-domain gate keys are derived (reading) / family clears
//    (writing false). The RegisterSetting calls STAY — they are where the instance-only
//    keys' defaults live and the key existence contract for the settings surfaces.
//    The runtime time trio (lastLaunchTime/lastTimePlayed/totalTimePlayed) is routed to
//    instance-state.json by the adapter — the reads/writes below are unchanged.
//  - The C++ registerPassthrough(setting, nullptr) calls (ConsoleMaxLines/
//    ConsoleOverflowStop) become plain registrations whose values the adapter resolves from
//    the user layer — the passthrough pair is global-only, instance writes are dropped.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;
using FlintLauncher.Core.Text;
using TruePath;

namespace FlintLauncher.Core.Instances;

/// <summary>Port of ShortcutTarget.</summary>
public enum ShortcutTarget : byte
{
    Desktop,
    Applications,
    Other,
}

/// <summary>Port of ShortcutData.</summary>
public readonly record struct ShortcutData(string Name, string FilePath, ShortcutTarget Target = ShortcutTarget.Other);

/// <summary>
/// Base class for instances. Implements many functions that are common between instances and
/// provides a standard interface for all instances.
/// </summary>
public abstract class BaseInstance : INotifyPropertyChanged
{
    /// <summary>Port of the BaseInstance::Status enum.</summary>
    public enum Status : byte
    {
        Present,
        Gone, // either nuked or invalidated
    }

    protected BaseInstance(SettingsObject settings, AbsolutePath rootDir)
    {
        InstanceRoot = rootDir;
        InstanceSettings = settings;

        InstanceSettings.RegisterSetting("name", "Unnamed Instance");
        InstanceSettings.RegisterSetting("iconKey", "default");
        InstanceSettings.RegisterSetting("notes", "");

        // the runtime time trio lives in instance-state.json — the adapter routes
        // these keys there; the registrations carry their zero defaults
        InstanceSettings.RegisterSetting("lastLaunchTime", 0L);
        InstanceSettings.RegisterSetting("totalTimePlayed", 0L);
        if (Variant.AsLong(InstanceSettings.Get("totalTimePlayed")) < 0)
        {
            InstanceSettings.Reset("totalTimePlayed");
        }

        InstanceSettings.RegisterSetting("lastTimePlayed", 0L);

        // a real string list (the old JSON text form parses through GetLinkedInstances)
        InstanceSettings.RegisterSetting("linkedInstances", Array.Empty<string>());
        InstanceSettings.RegisterSetting("shortcuts", "");
        InstanceSettings.RegisterSetting("uuid", "");

        var savedUuid = Variant.AsString(InstanceSettings.Get("uuid"));
        if (savedUuid.Length == 0)
        {
            RegenerateUuid();
        }
        else
        {
            Uuid = savedUuid;
        }

        // Game time override family (the gate key stays readable; its value is derived
        // from Misc.GameTime's members by the adapter — no OverrideSetting chain anymore).
        // The member keys register plain: the adapter resolves them instance ?? user layer.
        InstanceSettings.RegisterSetting("OverrideGameTime", false);
        InstanceSettings.RegisterSetting("ShowGameTime", true);
        InstanceSettings.RegisterSetting("RecordGameTime", true);
        InstanceSettings.RegisterSetting("CountGameTime", true);

        // NOTE: Sometimes InstanceType is already registered, as it was used to identify the
        // type of a locally stored instance. "OneSix" is the only instantiable type and the
        // record default (an unknown type never constructs a MinecraftInstance — InstanceList
        // pre-reads the type from instance.json/instance.cfg and yields NullInstance).
        if (InstanceSettings.GetSetting("InstanceType") is null)
        {
            InstanceSettings.RegisterSetting("InstanceType", "OneSix");
        }

        // Custom Commands (OverrideCommands family; the synonym covers the legacy spelling)
        InstanceSettings.RegisterSetting(["OverrideCommands", "OverrideLaunchCmd"], false);
        InstanceSettings.RegisterSetting("PreLoadCommand", "");
        InstanceSettings.RegisterSetting("PreLaunchCommand", "");
        InstanceSettings.RegisterSetting("WrapperCommand", "");
        InstanceSettings.RegisterSetting("PostExitCommand", "");

        // Console (OverrideConsole family)
        InstanceSettings.RegisterSetting("OverrideConsole", false);
        InstanceSettings.RegisterSetting("ShowConsole", false);
        InstanceSettings.RegisterSetting("AutoCloseConsole", false);
        InstanceSettings.RegisterSetting("ShowConsoleOnError", true);
        InstanceSettings.RegisterSetting("LogPrePostOutput", true);
        // passthrough pair: global-only values (the adapter resolves them from the user
        // layer); registered so the GetConsoleMaxLines/ShouldStopOnConsoleOverflow readers
        // keep finding the keys
        InstanceSettings.RegisterSetting("ConsoleMaxLines", 100000);
        InstanceSettings.RegisterSetting("ConsoleOverflowStop", true);

        // Managed Packs
        InstanceSettings.RegisterSetting("ManagedPack", false);
        InstanceSettings.RegisterSetting("ManagedPackType", "");
        InstanceSettings.RegisterSetting("ManagedPackID", "");
        InstanceSettings.RegisterSetting("ManagedPackName", "");
        InstanceSettings.RegisterSetting("ManagedPackVersionID", "");
        InstanceSettings.RegisterSetting("ManagedPackVersionName", "");
        InstanceSettings.RegisterSetting("ManagedPackURL", "");

        InstanceSettings.RegisterSetting("Profiler", "");
    }

    // ---------------------------------------------------------------- events (Qt signals)

    /// <summary>Port of the launchTaskChanged() signal (added with the launch pipeline).</summary>
    public event Action<BaseInstance>? LaunchTaskChanged;

    /// <summary>Raise LaunchTaskChanged (C# events can only be raised by the declaring type).</summary>
    protected void RaiseLaunchTaskChanged() => LaunchTaskChanged?.Invoke(this);

    /// <summary>INPC for the instance-view properties (name/icon/notes/running state/…):
    /// the standard clothing of the C++ propertiesChanged() signal. Property names are the
    /// setter-semantic names below ("Name", "IconKey", "IsRunning", …); an empty name means
    /// "the whole instance view changed" (group membership and similar external facts).</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raise PropertyChanged for one view property (the declaring type raises).</summary>
    private void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>The whole-instance-view notification (empty property name = all properties
    /// changed): the group-membership half of the C++ dataChanged() glue — group state lives
    /// on InstanceList, not here, so there is no property name to point at.</summary>
    public void NotifyChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>Port of runningStatusChanged().</summary>
    public event Action<bool>? RunningStatusChanged;

    /// <summary>Port of statusChanged().</summary>
    public event Action<Status, Status>? StatusChanged;

    // ------------------------------------------------------------- free functions (port)

    /// <summary>Port of the getConsoleMaxLines free function.</summary>
    public static int GetConsoleMaxLines(SettingsObject settings)
    {
        var lineSetting = settings.GetSetting("ConsoleMaxLines");
        var value = lineSetting?.Get();
        int maxLines;
        bool conversionOk;
        if (value is string s)
        {
            // QVariant::toInt(&ok) fails for non-numeric strings
            conversionOk = int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxLines);
        }
        else
        {
            conversionOk = true;
            maxLines = Variant.AsInt(value);
        }

        if (!conversionOk)
        {
            // (upstream logs a qWarning here)
            maxLines = Variant.AsInt(lineSetting?.Default);
        }

        return maxLines;
    }

    /// <summary>Port of the shouldStopOnConsoleOverflow free function.</summary>
    public static bool ShouldStopOnConsoleOverflow(SettingsObject settings)
    {
        return Variant.AsBool(settings.Get("ConsoleOverflowStop"));
    }

    // ------------------------------------------------------------------ ported surface

    public abstract void SaveNow();

    /// <summary>
    /// The instance has been invalidated - it is no longer tracked by the launcher for some
    /// reason, but it has not necessarily been deleted. Happens when the instance folder
    /// changes to some other location, or the instance is removed by external means.
    /// </summary>
    public void Invalidate()
    {
        ChangeStatus(Status.Gone);
    }

    /// <summary>The instance is leaving the live list (deleted on request, dropped as
    /// vanished, or the configured roots changed): release its watcher tenancy — lazily
    /// created folder models registered their directories on the shared space, and after
    /// this point those directories are gone or no longer belong to this instance.
    /// No-op without tenancy; the instance itself stays usable read-only.</summary>
    public virtual void ReleaseWatchTenancy()
    {
    }

    /// <summary>
    /// The instance's ID. The ID SHALL be determined by LAUNCHER internally. The ID IS
    /// guaranteed to be unique.
    /// </summary>
    public string Id() => InstanceRoot.FileName;

    public string Uuid { get; private set; } = "";

    public void RegenerateUuid()
    {
        // QUuid::createUuid().toString(QUuid::Id128): 32 hexadecimal digits without separators
        var newUuid = Guid.NewGuid().ToString("N");
        InstanceSettings.Set("uuid", newUuid);
        Uuid = newUuid;
    }

    public void SetMinecraftRunning(bool running)
    {
        if (!Variant.AsBool(Settings.Get("RecordGameTime")))
        {
            return;
        }

        if (running)
        {
            TimeStarted = DateTimeOffset.Now;
            SetLastLaunch(TimeStarted.Value.ToUnixTimeMilliseconds());
        }
        else
        {
            var timeEnded = DateTimeOffset.Now;
            // QDateTime::secsTo on an invalid (never started) datetime is 0
            var secondsPlayed = TimeStarted is { } timeStarted ? (long)(timeEnded - timeStarted).TotalSeconds : 0;

            var current = Variant.AsLong(Settings.Get("totalTimePlayed"));
            Settings.Set("totalTimePlayed", current + secondsPlayed);
            Settings.Set("lastTimePlayed", secondsPlayed);

            if (CountTimePlayed() && LauncherState is not null)
            {
                // the add happens inside the update so concurrent writers cannot lose time
                LauncherState.Update(s => s with
                {
                    Playtime = s.Playtime with { Total = s.Playtime.Total + secondsPlayed },
                });
            }

            RaisePropertyChanged("TotalTimePlayed");
        }
    }

    public void SetRunning(bool running)
    {
        if (running == IsRunning)
        {
            return;
        }

        IsRunning = running;

        RunningStatusChanged?.Invoke(running);
    }

    public bool IsRunning { get; private set; }

    public long TotalTimePlayed()
    {
        var current = Variant.AsLong(InstanceSettings.Get("totalTimePlayed"));
        if (IsRunning)
        {
            var timeNow = DateTimeOffset.Now;
            return current + (TimeStarted is { } timeStarted ? (long)(timeNow - timeStarted).TotalSeconds : 0);
        }

        return current;
    }

    public long LastTimePlayed()
    {
        if (IsRunning)
        {
            var timeNow = DateTimeOffset.Now;
            return TimeStarted is { } timeStarted ? (long)(timeNow - timeStarted).TotalSeconds : 0;
        }

        return Variant.AsLong(InstanceSettings.Get("lastTimePlayed"));
    }

    public bool CountTimePlayed() => Variant.AsBool(InstanceSettings.Get("CountGameTime"));

    public void ResetTimePlayed()
    {
        Settings.Reset("totalTimePlayed");
        Settings.Reset("lastTimePlayed");
    }

    /// <summary>get the type of this instance</summary>
    public string InstanceType() => Variant.AsString(InstanceSettings.Get("InstanceType"));

    /// <summary>Path to the instance's root directory.</summary>
    public AbsolutePath InstanceRoot { get; }

    /// <summary>Path to the instance's game root directory.</summary>
    public virtual AbsolutePath GameRoot => InstanceRoot;

    /// <summary>Path to the instance's mods directory.</summary>
    public abstract AbsolutePath ModsRoot { get; }

    public string Name() => Variant.AsString(InstanceSettings.Get("name"));

    public void SetName(string val)
    {
        // FIXME upstream: if no change, do not set. setting involves saving a file.
        InstanceSettings.Set("name", val);
        RaisePropertyChanged("Name");
    }

    // NOTE: the C++ setInstanceSyncLine moved the instance directory on rename
    // (syncInstanceDirName) so the dirname stayed a stable handle. The addressing chain
    // (inode → uuid → dirname) makes that move unnecessary — directory names stay
    // human-readable, a rename is a pure metadata write, and references hold via uuid.


    /// <summary>Register a created shortcut.</summary>
    public void RegisterShortcut(ShortcutData data)
    {
        var currentShortcuts = Shortcuts();
        currentShortcuts.Add(data);
        SetShortcuts(currentShortcuts);
    }

    public List<ShortcutData> Shortcuts()
    {
        var data = Variant.AsString(InstanceSettings.Get("shortcuts"));
        if (data.Length == 0)
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<ShortcutData>();
            foreach (var elem in document.RootElement.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!elem.TryGetProperty("name", out var name) || !elem.TryGetProperty("filePath", out var filePath) ||
                    !elem.TryGetProperty("target", out var target))
                {
                    continue;
                }

                var value = target.ValueKind == JsonValueKind.Number ? target.GetInt32() : -1;
                if (name.ValueKind != JsonValueKind.String || filePath.ValueKind != JsonValueKind.String ||
                    value < 0 || value >= 3)
                {
                    continue;
                }

                var shortcutName = name.GetString();
                var path = filePath.GetString();
                if (shortcutName is null || path is null)
                {
                    continue;
                }

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    // (upstream logs a qWarning here)
                    continue;
                }

                results.Add(new ShortcutData(shortcutName, path, (ShortcutTarget)value));
            }

            return results;
        }
    }

    public void SetShortcuts(IReadOnlyList<ShortcutData> shortcuts)
    {
        // FIXME upstream: if no change, do not set. setting involves saving a file.
        var array = new JsonArray();
        foreach (var elem in shortcuts)
        {
            var obj = new JsonObject
            {
                ["name"] = JsonValue.Create(elem.Name),
                ["filePath"] = JsonValue.Create(elem.FilePath),
                ["target"] = JsonValue.Create((int)elem.Target),
            };
            array.Add((JsonNode?)obj); // non-generic Add: AOT/trimming clean
        }

        InstanceSettings.Set("shortcuts", array.ToJsonString()); // QJsonDocument::Compact
    }

    /// <summary>Value used for instance window titles.</summary>
    public string WindowTitle() => FlintConfig.Default.LauncherDisplayName + ": " + Name();

    public string IconKey() => Variant.AsString(InstanceSettings.Get("iconKey"));

    public void SetIconKey(string val)
    {
        // FIXME upstream: if no change, do not set. setting involves saving a file.
        InstanceSettings.Set("iconKey", val);
        RaisePropertyChanged("IconKey");
    }

    /// <summary>Port of the iconUpdated() slot: reacts to the icon list changing a key.</summary>
    public void IconUpdated(string key)
    {
        if (IconKey() == key)
        {
            RaisePropertyChanged("IconKey");
        }
    }

    public string Notes() => Variant.AsString(InstanceSettings.Get("notes"));

    public void SetNotes(string val)
    {
        // FIXME upstream: if no change, do not set. setting involves saving a file.
        InstanceSettings.Set("notes", val);
        RaisePropertyChanged("Notes");
    }

    public string GetPreLoadCommand() => Variant.AsString(Settings.Get("PreLoadCommand"));

    public string GetPreLaunchCommand() => Variant.AsString(Settings.Get("PreLaunchCommand"));

    public string GetPostExitCommand() => Variant.AsString(Settings.Get("PostExitCommand"));

    public string GetWrapperCommand() => Variant.AsString(Settings.Get("WrapperCommand"));

    public bool IsManagedPack() => Variant.AsBool(InstanceSettings.Get("ManagedPack"));

    public string GetManagedPackType() => Variant.AsString(InstanceSettings.Get("ManagedPackType"));

    public string GetManagedPackId() => Variant.AsString(InstanceSettings.Get("ManagedPackID"));

    public string GetManagedPackName() => Variant.AsString(InstanceSettings.Get("ManagedPackName"));

    public string GetManagedPackVersionId() => Variant.AsString(InstanceSettings.Get("ManagedPackVersionID"));

    public string GetManagedPackVersionName() => Variant.AsString(InstanceSettings.Get("ManagedPackVersionName"));

    public void SetManagedPack(string type, string id, string name, string versionId, string version)
    {
        InstanceSettings.Set("ManagedPack", true);
        InstanceSettings.Set("ManagedPackType", type);
        InstanceSettings.Set("ManagedPackID", id);
        InstanceSettings.Set("ManagedPackName", name);
        InstanceSettings.Set("ManagedPackVersionID", versionId);
        InstanceSettings.Set("ManagedPackVersionName", version);

        if ((UserLayer.Java.AutomaticJavaSwitch ?? true) &&
            Variant.AsBool(InstanceSettings.Get("AutomaticJava")) &&
            Variant.AsBool(InstanceSettings.Get("OverrideJavaLocation")))
        {
            // translation of the C++ pair (gate=false + JavaPath=""): clear the instance
            // java-location family — every member falls back to the user layer. There is no
            // separate JavaPath="" write anymore: with the gate gone, an explicit empty
            // string would be a real (empty) override, the opposite of the intent.
            InstanceSettings.Set("OverrideJavaLocation", false);
        }
    }

    public virtual List<string> ExtraArguments()
    {
        return Commandline.SplitArgs(Variant.AsString(Settings.Get("JvmArgs")));
    }

    /// <summary>Traits. Normally inside the version, depends on instance implementation.</summary>
    public abstract IReadOnlySet<string> Traits();

    /// <summary>
    /// Gets the time that the instance was last launched. Stored in milliseconds since epoch.
    /// </summary>
    public long LastLaunch() => Variant.AsLong(InstanceSettings.Get("lastLaunchTime"));

    /// <summary>Sets the last launched time to 'val' milliseconds since epoch.</summary>
    public void SetLastLaunch(long val)
    {
        // FIXME upstream: if no change, do not set. setting involves saving a file.
        InstanceSettings.Set("lastLaunchTime", val);
        RaisePropertyChanged("LastLaunch");
    }

    public void SetLastLaunch() => SetLastLaunch(DateTimeOffset.Now.ToUnixTimeMilliseconds());

    /// <summary>
    /// Gets this instance's settings object. This settings object stores instance-specific
    /// settings. Reading may load the type-specific settings first (loadSpecificSettings).
    /// </summary>
    public virtual SettingsObject Settings
    {
        get
        {
            LoadSpecificSettings();

            return InstanceSettings;
        }
    }

    /// <summary>Loads settings specific to an instance type if they're not already loaded.</summary>
    public abstract void LoadSpecificSettings();

    /// <summary>returns a valid update task</summary>
    public abstract List<Func<CancellationToken, Task>> CreateUpdateTask();

    /// <summary>
    /// returns a valid launcher (the step chain for one launch).
    /// </summary>
    public abstract LaunchTask? CreateLaunchTask(AuthSession? account, MinecraftTarget? targetToJoin);

    /// <summary>returns the current launch task (if any)</summary>
    public LaunchTask? GetLaunchTask() => LaunchProcess;

    /// <summary>Create environment variables for running the instance.</summary>
    public abstract ProcessEnvironment CreateEnvironment();

    public abstract ProcessEnvironment CreateLaunchEnvironment();

    /// <summary>Returns the root folder to use for looking up log files.</summary>
    public abstract IReadOnlyList<AbsolutePath> GetLogFileSearchPaths();

    public abstract string GetStatusbarDescription();

    /// <summary>FIXME upstream: this really should be elsewhere...</summary>
    public abstract AbsolutePath InstanceConfigFolder();

    /// <summary>get variables this instance exports</summary>
    public abstract SortedDictionary<string, string> GetVariables();

    public virtual void UpdateRuntimeContext()
    {
        // NOOP
    }

    // NOTE: struct properties need an explicit construction here or System would stay null
    public RuntimeContext RuntimeContext { get; protected set; } = new();

    public bool HasVersionBroken => _hasBrokenVersion;

    public void SetVersionBroken(bool value)
    {
        if (_hasBrokenVersion != value)
        {
            _hasBrokenVersion = value;
            RaisePropertyChanged("HasBrokenVersion");
        }
    }

    public bool HasUpdateAvailable => _hasUpdate;

    public void SetUpdateAvailable(bool value)
    {
        if (_hasUpdate != value)
        {
            _hasUpdate = value;
            RaisePropertyChanged("HasUpdateAvailable");
        }
    }

    public bool HasCrashed => _crashed;

    public void SetCrashed(bool value)
    {
        if (_crashed != value)
        {
            _crashed = value;
            RaisePropertyChanged("HasCrashed");
        }
    }

    public virtual bool CanLaunch()
    {
        return !HasVersionBroken && !IsRunning;
    }

    public abstract bool CanEdit();

    public abstract bool CanExport();

    public bool ReloadSettings() => InstanceSettings.Reload();

    /// <summary>'print' a verbose description of the instance into a list of strings.</summary>
    public abstract List<string> VerboseDescription(AuthSession? session, MinecraftTarget? targetToJoin);

    public Status CurrentStatus() => _status;

    public List<string> GetLinkedInstances()
    {
        // the typed layer carries a real string array; a legacy JSON text (old cfg value
        // round-tripping through a plain SettingsObject double) still parses
        return InstanceSettings.Get("linkedInstances") is IReadOnlyList<string> list
            ? list.ToList()
            : SettingsJson.ToStringList(Variant.AsString(InstanceSettings.Get("linkedInstances")));
    }

    public void SetLinkedInstances(IReadOnlyList<string> list)
    {
        InstanceSettings.Set("linkedInstances", list);
    }

    public void AddLinkedInstanceId(string id)
    {
        // the persisted reference form is the uuid — a dirname input resolves at WRITE
        // time when <see cref="UuidResolver"/> is wired (InstanceList stamps it during
        // discovery). Without a resolver (bare fixtures, not-yet-discovered targets) the raw
        // value appends verbatim and converts at the next LoadList round, exactly like the
        // group-file members. The C++ lineage passed id() — the dirname; that form is NOT
        // carried over (the persistent handle is the uuid).
        var links = GetLinkedInstances();
        links.Add(UuidResolver?.Invoke(id) is { Length: > 0 } resolvedUuid ? resolvedUuid : id);
        SetLinkedInstances(links);
    }

    public bool RemoveLinkedInstanceId(string id)
    {
        var linkedInstances = GetLinkedInstances();
        var numRemoved = linkedInstances.RemoveAll(item => item == id);
        SetLinkedInstances(linkedInstances);
        return numRemoved > 0;
    }

    public bool IsLinkedToInstanceId(string id) => GetLinkedInstances().Contains(id);

    public bool IsLegacy()
    {
        var traits = Traits();
        return traits.Contains("legacyLaunch") || traits.Contains("alphaLaunch");
    }

    protected void ChangeStatus(Status newStatus)
    {
        var previous = CurrentStatus();
        if (previous != newStatus)
        {
            _status = newStatus;
            StatusChanged?.Invoke(previous, newStatus);
        }
    }

    /// <summary>The resolved user layer (flint.json) behind the typed settings stack — the
    /// C++ reads of APPLICATION->settings() on the instance side. Defaults when the settings
    /// object is not the typed adapter (bare test doubles).</summary>
    protected FlintConfigFile UserLayer =>
        (InstanceSettings as InstanceJsonSettingsObject)?.UserLayer ?? FlintConfigFile.Default();

    /// <summary>The live user-layer store (flint.json) when the settings object is the typed
    /// adapter — handed to LaunchServices so the launch pipeline reads the same stack.</summary>
    protected FlintConfigStore? UserConfigStore =>
        (InstanceSettings as InstanceJsonSettingsObject)?.UserConfig;

    protected bool IsSpecificSettingsLoaded() => _specificSettingsLoaded;

    protected void SetSpecificSettingsLoaded(bool loaded) => _specificSettingsLoaded = loaded;

    // ---------------------------------------------------------------------- data

    /// <summary>The backing store of <see cref="Settings"/>; shared with subclasses that
    /// register their instance-type keys (MinecraftInstance).</summary>
    protected readonly SettingsObject InstanceSettings;

    /// <summary>std::unique_ptr&lt;LaunchTask&gt; m_launchProcess.</summary>
    protected LaunchTask? LaunchProcess;

    protected DateTimeOffset? TimeStarted;

    /// <summary>The launcher runtime state (launcher-state.json) — the playtime
    /// accumulation lands in <c>Playtime.Total</c> (the former TotalPlayTime settings key).
    /// Null skips the global accumulation; the instance list stamps it at
    /// discovery.</summary>
    public LauncherStateStore? LauncherState { get; set; }

    /// <summary>Resolves an instance reference (dirname or uuid) to the referenced
    /// instance's uuid — the persisted linkedInstances form. Port-internal wiring (no C++
    /// counterpart): the instance list stamps it at discovery so the write path below
    /// converts dirname inputs instead of persisting them. Null (bare fixtures) means
    /// references append verbatim; the LoadList conversion pass is the backstop.</summary>
    internal Func<string, string?>? UuidResolver { get; set; }

    private Status _status = Status.Present;
    private bool _crashed;
    private bool _hasUpdate;
    private bool _hasBrokenVersion;

    private bool _specificSettingsLoaded;
}
