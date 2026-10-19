// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/PackProfile.{h,cpp} (plus PackProfile_p.h inlined): the instance
// component list manager — mmc-pack.json / patches/*.json persistence, component operations
// and the LaunchProfile assembly.
//
// Deviations from the C++ original:
//  - The QAbstractListModel surface (data()/setData()/headerData()/rowCount()/flags(), the
//    begin*/end* notifications and the interactionDisabled bookkeeping) is UI plumbing and is
//    not ported; the notifications that reached the UI become the plain events
//    <see cref="MinecraftChanged"/> and <see cref="DataChanged"/>.
//  - The MinecraftInstance dependency is reduced to the <see cref="IPackProfileEnv"/> surface
//    (instance root, instance name, runtime context); MinecraftInstance will implement it in
//    P3. A plain constructor taking the instance root is available for tests/standalone use.
//  - APPLICATION->metadataIndex() becomes the per-profile <see cref="MetadataIndex"/> (in C++
//    all profiles share the application-wide index; pass the same instance around to share).
//  - scheduleSave()'s 5-second QTimer becomes a dirty flag only: saving happens through
//    <see cref="SaveNow"/> (and on <see cref="Dispose"/>); the instance layer (P3) can wire a
//    timer on top.
//  - reload()/resolve() await the ComponentUpdateTask (C++ start()s it and returns, results
//    arriving via signals); the returned Task makes the completion awaitable.
//  - setComponentVersion() is async for the same reason (important changes trigger a resolve).
//  - Not ported: setOldConfigVersion() and writeOverrideOrders() (declared-but-undefined
//    upstream), updateLatestMinecraft() (instance update flow, P3) and the model roles.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Net;
using Index = FlintLauncher.Core.Meta.Index;

namespace FlintLauncher.Core.Minecraft;

/// <summary>
/// Port of the MinecraftInstance surface PackProfile/Component actually use (instanceRoot(),
/// name(), runtimeContext(), jarModsDir()/getLocalLibraryPath() are all derived from the
/// instance root). Implemented by MinecraftInstance in P3.
/// </summary>
public interface IPackProfileEnv
{
    /// <summary>The instance folder holding mmc-pack.json, patches/, jarmods/ and libraries/.</summary>
    string InstanceRoot { get; }

    /// <summary>Instance display name (C++ name(); only used for log lines).</summary>
    string InstanceName { get; }

    /// <summary>C++ runtimeContext(); the instance keeps this in sync with its settings.</summary>
    RuntimeContext RuntimeContext { get; }
}

/// <summary>Minimal IPackProfileEnv for standalone/test usage (name empty, default runtime context).</summary>
public sealed class BasicPackProfileEnv(string instanceRoot, string instanceName = "", RuntimeContext runtimeContext = default) : IPackProfileEnv
{
    public string InstanceRoot { get; } = instanceRoot;

    public string InstanceName { get; } = instanceName;

    public RuntimeContext RuntimeContext { get; } = runtimeContext;
}

public sealed class PackProfile(IPackProfileEnv env, Index? metadataIndex = null) : IDisposable
{
    public enum MoveDirection
    {
        MoveUp,
        MoveDown,
    }

    /// <summary>Port of PackProfile::Result (the C++ factory names are kept; data members renamed).</summary>
    public readonly struct Result
    {
        private Result(bool ok, string error)
        {
            Ok = ok;
            ErrorMessage = error;
        }

        /// <summary>The C++ implicit conversion to bool reads this member.</summary>
        public bool Ok { get; }

        public string ErrorMessage { get; }

        public static Result Success() => new(true, "");

        public static Result Error(string errorMessage) => new(false, errorMessage);

        /// <summary>The C++ implicit conversion to bool.</summary>
        public static implicit operator bool(Result result) => result.Ok;
    }

    private const int CurrentComponentsFileVersion = 1;

    /// <summary>the launch profile (volatile, temporary thing created on demand)</summary>
    private LaunchProfile? profile;

    private readonly List<Component> _components = [];
    private readonly Dictionary<string, Component> _componentIndex = new(StringComparer.Ordinal);

    private bool dirty;

    private ComponentUpdateTask? updateTask;

    private bool loaded;

    public PackProfile(string instanceRoot, Index? metadataIndex = null)
        : this(new BasicPackProfileEnv(instanceRoot), metadataIndex) { }

    public void Dispose()
    {
        // ~PackProfile() flushes a scheduled save
        SaveNow();
    }

    /// <summary>Port of the minecraftChanged() signal (UI plumbing).</summary>
    public event Action<PackProfile>? MinecraftChanged;

    /// <summary>Merged port of the QAbstractListModel dataChanged() notifications (UI plumbing).</summary>
    public event Action<PackProfile>? DataChanged;

    // ---------------------------------------------------------------- environment

    public string InstanceName => env.InstanceName;

    /// <summary>C++ PackProfile::runtimeContext() (helper method, returns the instance's context).</summary>
    public RuntimeContext RuntimeContext() => env.RuntimeContext;

    /// <summary>
    /// The metadata index (C++: the APPLICATION->metadataIndex() singleton; shared by all
    /// profiles that are given the same Index instance).
    /// </summary>
    public Index MetadataIndex { get; } = metadataIndex ?? new Index();

    /// <summary>Wire the metadata entities to a runtime-owned cache (the composition root
    /// calls this after stamping the instance; entities created later inherit it through the
    /// Index/VersionList adoption points).</summary>
    public void SetMetadataCache(HttpMetaCache cache) => MetadataIndex.Cache = cache;

    /// <summary>The component list in profile order (C++ d->components; the update task reads it).</summary>
    public IReadOnlyList<Component> Components => _components;

    /// <summary>The components by uid (C++ d->componentIndex).</summary>
    public IReadOnlyDictionary<string, Component> ComponentIndex => _componentIndex;

    // ---------------------------------------------------------------- paths

    private string ComponentsFilePath() => FileSystem.PathCombine(env.InstanceRoot, "mmc-pack.json");

    private string PatchesPattern() => FileSystem.PathCombine(env.InstanceRoot, "patches", "%1.json");

    public string PatchFilePathForUid(string uid) => FileSystem.PathCombine(env.InstanceRoot, "patches", uid + ".json");

    /// <summary>C++ MinecraftInstance::jarModsDir() (lives under the instance root, not the game root).</summary>
    private string JarModsDir() => FileSystem.PathCombine(env.InstanceRoot, "jarmods");

    /// <summary>C++ MinecraftInstance::getLocalLibraryPath().</summary>
    private string LocalLibraryPath() => FileSystem.PathCombine(env.InstanceRoot, "libraries");

    // ---------------------------------------------------------------- save/load logic

    /// <summary>if there is a save scheduled, do it now.</summary>
    public void SaveNow()
    {
        // (the C++ stops the deferred-save QTimer on success here)
        if (SaveIsScheduled())
        {
            SaveInternal();
        }
    }

    public bool SaveIsScheduled() => dirty;

    /// <summary>call this to explicitly mark the component list as loaded - used to build a new component list from scratch.</summary>
    public void BuildingFromScratch()
    {
        loaded = true;
        dirty = true;
    }

    private void ScheduleSave()
    {
        if (!loaded)
        {
            // "Component list should never save if it didn't successfully load"
            return;
        }

        // NOTE deviation: the C++ starts a 5s single-shot QTimer here; saving happens through
        // SaveNow()/Dispose() instead (no event loop at this layer)
        dirty = true;
    }

    private bool SaveInternal()
    {
        var filename = ComponentsFilePath();
        if (SavePackProfile(filename, _components))
        {
            dirty = false;
            return true;
        }

        return false;
    }

    // internal instead of private (C++ keeps it private) so the test project can exercise the
    // persistence layer without going through reload()/resolve()
    internal Result Load()
    {
        var filename = ComponentsFilePath();

        // load the new component list and swap it with the current one...
        var newComponents = new List<Component>();
        Result result = LoadPackProfile(this, filename, PatchesPattern(), newComponents);
        if (!result)
        {
            return result;
        }

        _components.Clear();
        _componentIndex.Clear();
        foreach (var component in newComponents)
        {
            if (_componentIndex.ContainsKey(component.Uid))
            {
                // "Ignoring duplicate component entry"
                continue;
            }

            component.DataChanged += OnComponentDataChanged;
            _components.Add(component);
            _componentIndex[component.Uid] = component;
        }

        loaded = true;
        return Result.Success();
    }

    /// <summary>
    /// reload the list, reload all components, resolve dependencies. Unlike the C++ original
    /// (which returns once the resolve task is started), the returned Task completes when the
    /// resolution finished.
    /// </summary>
    public async Task<Result> ReloadAsync(NetMode netmode, CancellationToken cancellationToken = default)
    {
        // Do not reload when the update/resolve task is running. It is in control.
        if (updateTask is not null)
        {
            if (updateTask.GetNetMode() == netmode)
            {
                return Result.Success();
            }

            // https://github.com/PrismLauncher/PrismLauncher/issues/5209
            // FIXME upstream: HACK HACK HACK
            updateTask.Abort();
            updateTask = null;
        }

        // flush any scheduled saves to not lose state
        SaveNow();

        // FIXME upstream: differentiate when a reapply is required by propagating state from components
        InvalidateLaunchProfile();

        var result = Load();
        if (!result)
        {
            return result;
        }

        await Resolve(netmode, cancellationToken);
        return Result.Success();
    }

    /// <summary>get current running task...</summary>
    public ComponentUpdateTask? GetCurrentTask() => updateTask;

    /// <summary>
    /// resolve all components (starts a ComponentUpdateTask). The returned Task completes when
    /// the task reaches a terminal state (C++ notifies through signals instead).
    /// </summary>
    public Task Resolve(NetMode netmode, CancellationToken cancellationToken = default)
    {
        var updateTask = new ComponentUpdateTask(ComponentUpdateTask.Mode.Resolution, netmode, this);
        this.updateTask = updateTask;
        updateTask.Succeeded += _ => UpdateSucceeded();
        updateTask.Failed += (_, _) => UpdateFailed();
        updateTask.Aborted += _ => UpdateFailed();
        return updateTask.StartAsync(cancellationToken);
    }

    private void UpdateSucceeded()
    {
        updateTask = null;
        InvalidateLaunchProfile();
    }

    private void UpdateFailed()
    {
        updateTask = null;
        InvalidateLaunchProfile();
    }

    // END: save/load

    // BEGIN: component file format

    // The mmc-pack write wire (docs/JSON-MIGRATION.md, the Qt write-tail batch): the typed
    // records through the source-generated context; the member conditions of the former
    // JsonObject inserts become null members (WhenWritingNull skips them).

    internal sealed record RequireOutJson
    {
        public string Uid { get; init; } = "";

        [JsonPropertyName("equals")] public string? EqualsVersion { get; init; }

        public string? Suggests { get; init; }
    }

    internal sealed record ComponentOutJson
    {
        public string Uid { get; init; } = "";

        public string? Version { get; init; }

        public bool? DependencyOnly { get; init; }

        public bool? Important { get; init; }

        public bool? Disabled { get; init; }

        public string? CachedVersion { get; init; }

        public string? CachedName { get; init; }

        public List<RequireOutJson>? CachedRequires { get; init; }

        public List<RequireOutJson>? CachedConflicts { get; init; }

        public bool? CachedVolatile { get; init; }
    }

    internal sealed record PackConfigOutJson
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; init; }

        public List<ComponentOutJson>? Components { get; init; }
    }

    /// <summary>Require list → wire (the former MetaJsonFormat.SerializeRequires shape:
    /// uid always, equals/suggests when non-empty).</summary>
    private static List<RequireOutJson>? RequiresToWire(SortedSet<Require> requires)
    {
        if (requires.Count == 0)
        {
            return null;
        }

        return requires.Select(require => new RequireOutJson
        {
            Uid = require.Uid,
            EqualsVersion = require.EqualsVersion.Length != 0 ? require.EqualsVersion : null,
            Suggests = require.Suggests.Length != 0 ? require.Suggests : null,
        }).ToList();
    }

    private static ComponentOutJson ComponentToJsonV1(Component component)
    {
        return new ComponentOutJson
        {
            // critical
            Uid = component.Uid,
            Version = component.Version.Length != 0 ? component.Version : null,
            DependencyOnly = component.DependencyOnly ? true : null,
            Important = component.Important ? true : null,
            Disabled = component.Disabled ? true : null,
            // cached
            CachedVersion = component.CachedVersion.Length != 0 ? component.CachedVersion : null,
            CachedName = component.CachedName.Length != 0 ? component.CachedName : null,
            CachedRequires = RequiresToWire(component.CachedRequires),
            CachedConflicts = RequiresToWire(component.CachedConflicts),
            CachedVolatile = component.CachedVolatile ? true : null,
        };
    }

    /// <summary>The mmc-pack READ wire (the mirror of the write records above; the same
    /// shared MetaRequireJson carries the cachedRequires/cachedConflicts entries as every
    /// other requires array). Read-only — the write side is the *OutJson set.</summary>
    internal sealed record PackConfigJson
    {
        [JsonPropertyName("formatVersion")]
        [JsonConverter(typeof(QtCoercingIntConverter))]
        public int FormatVersion { get; set; }

        public List<PackComponentJson>? Components { get; set; }
    }

    internal sealed record PackComponentJson
    {
        /// <summary>Required — a missing member fails the file (the former RequireString).</summary>
        public string? Uid { get; set; }

        [JsonConverter(typeof(QtCoercingStringConverter))]
        public string Version { get; set; } = "";

        [JsonConverter(typeof(QtCoercingBoolConverter))]
        public bool DependencyOnly { get; set; }

        [JsonConverter(typeof(QtCoercingBoolConverter))]
        public bool Important { get; set; }

        [JsonConverter(typeof(QtCoercingBoolConverter))]
        public bool Disabled { get; set; }

        [JsonConverter(typeof(QtCoercingStringConverter))]
        public string CachedVersion { get; set; } = "";

        [JsonConverter(typeof(QtCoercingStringConverter))]
        public string CachedName { get; set; } = "";

        public List<MetaRequireJson>? CachedRequires { get; set; }

        public List<MetaRequireJson>? CachedConflicts { get; set; }

        [JsonConverter(typeof(QtCoercingBoolConverter))]
        public bool Volatile { get; set; }
    }

    private static Component ComponentFromJsonV1(PackProfile parent, PackComponentJson wire)
    {
        // critical — the uid member is required (the former RequireString throw), and a
        // null list entry is the former "Component must be an object." throw
        var component = new Component(parent,
            (wire ?? throw new JsonParseException("Component must be an object.")).Uid
            ?? throw new JsonParseException("'uid' is not a string"))
        {
            Version = wire.Version,
            DependencyOnly = wire.DependencyOnly,
            Important = wire.Important,
            // cached
            // TODO upstream @RESILIENCE: ignore invalid values/structure here?
            CachedVersion = wire.CachedVersion,
            CachedName = wire.CachedName
        };

        MetaJsonFormat.ParseRequires(wire.CachedRequires, component.CachedRequires, "cachedRequires");
        MetaJsonFormat.ParseRequires(wire.CachedConflicts, component.CachedConflicts, "cachedConflicts");
        // NOTE upstream bug kept: the loader reads "volatile" while the saver writes "cachedVolatile"
        component.CachedVolatile = wire.Volatile;
        component.SetEnabled(!wire.Disabled);
        return component;
    }

    /// <summary>Save the given component container data to a file.</summary>
    private static bool SavePackProfile(string filename, IReadOnlyList<Component> container)
    {
        var doc = new PackConfigOutJson
        {
            FormatVersion = CurrentComponentsFileVersion,
            Components = container.Select(ComponentToJsonV1).ToList(),
        };
        try
        {
            FileSystem.Write(filename,
                JsonSerializer.SerializeToUtf8Bytes(doc, PackConfigJsonContext.Default.PackConfigOutJson));
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Read the given file into the component container.</summary>
    private static Result LoadPackProfile(PackProfile parent, string filename, string componentJsonPattern,
        List<Component> container)
    {
        if (!File.Exists(filename))
        {
            return Result.Error($"Components file {filename} doesn't exist. This should never happen.");
        }

        string data;
        try
        {
            data = File.ReadAllText(filename);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result.Error($"Couldn't open {filename} for reading: {e.Message}");
        }

        // and it's valid JSON (the syntax gate keeps its distinct error message; the typed
        // read below reports the kind/shape failures as "bad file format")
        try
        {
            JsonNode.Parse(data);
        }
        catch (JsonException e)
        {
            return Result.Error($"Couldn't parse {filename} as json: {e.Message}");
        }

        // and then read it and process it if all above is true.
        try
        {
            var doc = JsonSerializer.Deserialize(data, PackConfigJsonContext.Default.PackConfigJson);
            if (doc is null)
            {
                throw new JsonParseException("the document is null");
            }

            // check order file version.
            if (doc.FormatVersion != CurrentComponentsFileVersion)
            {
                throw new JsonParseException($"Invalid component file version, expected {CurrentComponentsFileVersion}");
            }

            foreach (var componentWire in doc.Components ?? [])
            {
                container.Add(ComponentFromJsonV1(parent, componentWire));
            }
        }
        catch (Exception e) when (e is JsonParseException or JsonException)
        {
            container.Clear();
            return Result.Error($"Couldn't parse {filename} : bad file format");
        }

        return Result.Success();
    }

    // END: component file format

    // ---------------------------------------------------------------- component ops

    /// <summary>Add the component to the internal list of patches.</summary>
    public void AppendComponent(Component component)
    {
        InsertComponent(_components.Count, component);
    }

    /// <summary>insert component so that its index is ideally the specified one</summary>
    public void InsertComponent(int index, Component component)
    {
        var id = component.GetId();
        if (id.Length == 0)
        {
            return;
        }

        if (_componentIndex.ContainsKey(id))
        {
            return;
        }

        // QList::insert() would assert beyond the end upstream; clamp like the release build does
        index = Math.Clamp(index, 0, _components.Count);
        _components.Insert(index, component);
        _componentIndex[id] = component;
        component.DataChanged += OnComponentDataChanged;
        ScheduleSave();
    }

    private void OnComponentDataChanged(Component component)
    {
        if (component.GetId() == "net.minecraft")
        {
            MinecraftChanged?.Invoke(this);
        }

        // (the C++ emits the model row dataChanged here)
        DataChanged?.Invoke(this);
        ScheduleSave();
    }

    /// <summary>remove component file # - including files/records</summary>
    public bool Remove(int index)
    {
        var patch = GetComponent(index);
        if (patch is null || !patch.IsRemovable())
        {
            return false;
        }

        if (!RemoveComponentInternal(patch))
        {
            return false;
        }

        _components.RemoveAt(index);
        _componentIndex.Remove(patch.GetId());
        InvalidateLaunchProfile();
        ScheduleSave();
        return true;
    }

    /// <summary>remove component file by id - including files/records</summary>
    public bool Remove(string id)
    {
        for (var i = 0; i < _components.Count; i++)
        {
            if (_components[i].GetId() == id)
            {
                return Remove(i);
            }
        }

        return false;
    }

    public bool Customize(int index)
    {
        var patch = GetComponent(index);
        if (patch is null || !patch.IsCustomizable())
        {
            return false;
        }

        if (!patch.Customize())
        {
            return false;
        }

        InvalidateLaunchProfile();
        ScheduleSave();
        return true;
    }

    public bool RevertToBase(int index)
    {
        var patch = GetComponent(index);
        if (patch is null || !patch.IsRevertible())
        {
            return false;
        }

        if (!patch.Revert())
        {
            return false;
        }

        InvalidateLaunchProfile();
        ScheduleSave();
        return true;
    }

    /// <summary>get the profile component by id</summary>
    public Component? GetComponent(string id)
    {
        return _componentIndex.GetValueOrDefault(id);
    }

    /// <summary>get the profile component by index</summary>
    public Component? GetComponent(int index)
    {
        return index >= 0 && index < _components.Count ? _components[index] : null;
    }

    public void Move(int index, MoveDirection direction)
    {
        int theirIndex;
        if (direction == MoveDirection.MoveUp)
        {
            theirIndex = index - 1;
        }
        else
        {
            theirIndex = index + 1;
        }

        if (index < 0 || index >= _components.Count)
        {
            return;
        }

        if (theirIndex >= _components.Count)
        {
            theirIndex = _components.Count - 1;
        }

        if (theirIndex == -1)
        {
            theirIndex = _components.Count - 1;
        }

        if (index == theirIndex)
        {
            return;
        }

        var from = GetComponent(index);
        var to = GetComponent(theirIndex);

        if (from is null || to is null || !to.IsMoveable() || !from.IsMoveable())
        {
            return;
        }

        (_components[index], _components[theirIndex]) = (_components[theirIndex], _components[index]);
        InvalidateLaunchProfile();
        ScheduleSave();
    }

    public void InvalidateLaunchProfile()
    {
        profile = null;
    }

    // ---------------------------------------------------------------- installers

    /// <summary>install more jar mods</summary>
    public bool InstallJarMods(IReadOnlyList<string> selectedFiles)
    {
        return InstallJarModsInternal(selectedFiles);
    }

    /// <summary>install a jar/zip as a replacement for the main jar</summary>
    public bool InstallCustomJar(string selectedFile)
    {
        return InstallCustomJarInternal(selectedFile);
    }

    /// <summary>install MMC/Prism component files</summary>
    public bool InstallComponents(IReadOnlyList<string> selectedFiles)
    {
        var patchDir = FileSystem.PathCombine(env.InstanceRoot, "patches");
        if (!FileSystem.EnsureFolderPathExists(patchDir))
        {
            return false;
        }

        var result = true;
        foreach (var source in selectedFiles)
        {
            var versionFile = ProfileUtils.ParseJsonFile(source, requireOrder: false);
            var target = FileSystem.PathCombine(patchDir, versionFile.Uid + ".json");

            if (!FileSystem.Copy(source, target))
            {
                result = false;
                continue;
            }

            AppendComponent(new Component(this, versionFile.Uid, versionFile));
        }

        ScheduleSave();
        InvalidateLaunchProfile();

        return result;
    }

    /// <summary>install Java agent files</summary>
    public bool InstallAgents(IReadOnlyList<string> selectedFiles)
    {
        return InstallAgentsInternal(selectedFiles);
    }

    public bool InstallEmpty(string uid, string name)
    {
        var patchDir = FileSystem.PathCombine(env.InstanceRoot, "patches");
        if (!FileSystem.EnsureFolderPathExists(patchDir))
        {
            return false;
        }

        var f = new VersionFile
        {
            Name = name,
            Uid = uid,
            Version = "1"
        };
        var patchFileName = FileSystem.PathCombine(patchDir, uid + ".json");
        try
        {
            FileSystem.Write(patchFileName, Encoding.UTF8.GetBytes(OneSixVersionFormat.SerializeVersionFile(f)));
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return false;
        }

        AppendComponent(new Component(this, f.Uid, f));
        ScheduleSave();
        InvalidateLaunchProfile();
        return true;
    }

    private bool RemoveComponentInternal(Component patch)
    {
        var ok = true;
        // first, remove the patch file. this ensures it's not used anymore
        var fileName = patch.GetFilename();
        if (fileName.Length != 0 && File.Exists(fileName) && !FileSystem.DeletePath(fileName))
        {
            return false;
        }

        // FIXME upstream: we need a generic way of removing local resources, not just jar mods...
        void PreRemoveJarMod(Library jarMod)
        {
            if (!jarMod.IsLocal())
            {
                return;
            }

            var jar = new List<string>();
            var temp1 = new List<string>();
            var temp2 = new List<string>();
            var temp3 = new List<string>();
            jarMod.GetApplicableFiles(RuntimeContext(), jar, temp1, temp2, temp3, JarModsDir());
            if (jar.Count != 0 && File.Exists(jar[0]) && !FileSystem.DeletePath(jar[0]))
            {
                ok = false;
            }
        }

        var vFile = patch.GetVersionFile();
        if (vFile is not null)
        {
            foreach (var jarmod in vFile.JarMods)
            {
                PreRemoveJarMod(jarmod);
            }
        }

        return ok;
    }

    private bool InstallJarModsInternal(IReadOnlyList<string> filepaths)
    {
        var patchDir = FileSystem.PathCombine(env.InstanceRoot, "patches");
        if (!FileSystem.EnsureFolderPathExists(patchDir))
        {
            return false;
        }

        if (!FileSystem.EnsureFolderPathExists(JarModsDir()))
        {
            return false;
        }

        foreach (var filepath in filepaths)
        {
            var id = Guid.NewGuid().ToString(); // QUuid::createUuid().toString(QUuid::WithoutBraces)
            var targetFilename = id + ".jar";
            var targetId = "custom.jarmod." + id;
            var targetName = CompleteBaseName(filepath) + " (jar mod)";
            var finalPath = FileSystem.PathCombine(JarModsDir(), targetFilename);

            // (the C++ copies sourceInfo.absoluteFilePath(); FileSystem.Copy copies a file
            // source onto the full dst path)
            if (!FileSystem.Copy(filepath, finalPath))
            {
                return false;
            }

            var f = new VersionFile();
            var jarMod = new Library
            {
                Name = new GradleSpecifier("custom.jarmods:" + id + ":1"),
                FilenameOverride = targetFilename,
                DisplayNameValue = CompleteBaseName(filepath),
                Hint = "local"
            };
            f.JarMods.Add(jarMod);
            f.Name = targetName;
            f.Uid = targetId;
            var patchFileName = FileSystem.PathCombine(patchDir, targetId + ".json");

            try
            {
                FileSystem.Write(patchFileName, Encoding.UTF8.GetBytes(OneSixVersionFormat.SerializeVersionFile(f)));
            }
            catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                return false;
            }

            AppendComponent(new Component(this, f.Uid, f));
        }

        ScheduleSave();
        InvalidateLaunchProfile();
        return true;
    }

    private bool InstallCustomJarInternal(string filepath)
    {
        var patchDir = FileSystem.PathCombine(env.InstanceRoot, "patches");
        if (!FileSystem.EnsureFolderPathExists(patchDir))
        {
            return false;
        }

        var libDir = LocalLibraryPath();
        if (!FileSystem.EnsureFolderPathExists(libDir))
        {
            return false;
        }

        var specifier = new GradleSpecifier("custom:customjar:1");
        var targetFilename = specifier.GetFileName() ?? "";
        var targetId = specifier.ArtifactId;
        var targetName = CompleteBaseName(filepath) + " (custom jar)";
        var finalPath = FileSystem.PathCombine(libDir, targetFilename);

        if (File.Exists(finalPath) && !FileSystem.DeletePath(finalPath))
        {
            return false;
        }

        if (!FileSystem.Copy(filepath, finalPath))
        {
            return false;
        }

        var f = new VersionFile();
        var jarMod = new Library
        {
            Name = specifier,
            DisplayNameValue = CompleteBaseName(filepath),
            Hint = "local"
        };
        f.MainJar = jarMod;
        f.Name = targetName;
        f.Uid = targetId;
        var patchFileName = FileSystem.PathCombine(patchDir, targetId + ".json");

        try
        {
            FileSystem.Write(patchFileName, Encoding.UTF8.GetBytes(OneSixVersionFormat.SerializeVersionFile(f)));
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return false;
        }

        AppendComponent(new Component(this, f.Uid, f));

        ScheduleSave();
        InvalidateLaunchProfile();
        return true;
    }

    private bool InstallAgentsInternal(IReadOnlyList<string> filepaths)
    {
        // FIXME upstream: code duplication
        var patchDir = FileSystem.PathCombine(env.InstanceRoot, "patches");
        if (!FileSystem.EnsureFolderPathExists(patchDir))
        {
            return false;
        }

        var libDir = LocalLibraryPath();
        if (!FileSystem.EnsureFolderPathExists(libDir))
        {
            return false;
        }

        foreach (var source in filepaths)
        {
            var id = Guid.NewGuid().ToString();
            var targetBaseName = id + ".jar";
            var targetId = "custom.agent." + id;
            var targetName = CompleteBaseName(source) + " (agent)";
            var target = FileSystem.PathCombine(LocalLibraryPath(), targetBaseName);

            if (!FileSystem.Copy(source, target))
            {
                return false;
            }

            var versionFile = new VersionFile();

            var agent = new Library
            {
                Name = new GradleSpecifier("custom.agents:" + id + ":1"),
                FilenameOverride = targetBaseName,
                DisplayNameValue = CompleteBaseName(source),
                Hint = "local"
            };

            versionFile.Agents.Add(new Agent(agent, ""));
            versionFile.Name = targetName;
            versionFile.Uid = targetId;

            try
            {
                FileSystem.Write(FileSystem.PathCombine(patchDir, targetId + ".json"),
                    Encoding.UTF8.GetBytes(OneSixVersionFormat.SerializeVersionFile(versionFile)));
            }
            catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                return false;
            }

            AppendComponent(new Component(this, versionFile.Uid, versionFile));
        }

        ScheduleSave();
        InvalidateLaunchProfile();

        return true;
    }

    /// <summary>QFileInfo::completeBaseName(): the file name up to the last dot.</summary>
    private static string CompleteBaseName(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    // ---------------------------------------------------------------- profile

    /// <summary>apply the component patches. Catches all the errors and returns null for failure.</summary>
    public LaunchProfile? GetProfile()
    {
        if (profile is null)
        {
            try
            {
                var profile = new LaunchProfile();
                foreach (var file in _components)
                {
                    file.ApplyTo(profile);
                }

                this.profile = profile;
            }
            catch (Exception e) when (e is JsonParseException or MetaParseException or FileSystemException)
            {
                return null;
            }
        }

        return profile;
    }

    // ---------------------------------------------------------------- versions

    public async Task<bool> SetComponentVersion(string uid, string version, bool important = false,
        CancellationToken cancellationToken = default)
    {
        if (_componentIndex.TryGetValue(uid, out var component))
        {
            // set existing
            if (component.Revert())
            {
                // set new version
                var oldVersion = component.GetVersion();
                component.SetVersion(version);
                component.SetImportant(important);

                if (important)
                {
                    component.SetUpdateAction(new UpdateAction.ImportantChanged(oldVersion));
                    await Resolve(NetMode.Online, cancellationToken);
                }

                return true;
            }

            return false;
        }

        // add new
        var newComponent = new Component(this, uid)
        {
            Version = version,
            Important = important,
        };
        AppendComponent(newComponent);
        return true;
    }

    public string GetComponentVersion(string uid)
    {
        return _componentIndex.TryGetValue(uid, out var component) ? component.GetVersion() : "";
    }

    // ---------------------------------------------------------------- mod loaders

    public ModLoaderType? GetModLoaders()
    {
        ModLoaderType result = ModLoaderType.None;
        var hasAnyLoader = false;

        foreach (var (uid, entry) in Component.KnownModloaders)
        {
            var c = GetComponent(uid);
            if (c is not null && c.IsEnabled())
            {
                result |= entry.Type;
                hasAnyLoader = true;
            }
        }

        if (!hasAnyLoader)
        {
            return null;
        }

        return result;
    }

    /// <summary>this returns additional loaders (Quilt supports fabric and NeoForge supports Forge)</summary>
    public ModLoaderType? GetSupportedModLoaders()
    {
        var loadersOpt = GetModLoaders();
        if (loadersOpt is not { } loaders)
        {
            return null;
        }

        // TODO upstream: remove this or add version condition once Quilt drops official Fabric support
        if ((loaders & ModLoaderType.Quilt) != 0)
        {
            loaders |= ModLoaderType.Fabric;
        }

        if (GetComponentVersion("net.minecraft") == "1.20.1" && (loaders & ModLoaderType.NeoForge) != 0)
        {
            loaders |= ModLoaderType.Forge;
        }

        return loaders;
    }

    public List<ModLoaderType> GetModLoadersList()
    {
        var result = new List<ModLoaderType>();
        foreach (var c in _components)
        {
            if (c.IsEnabled() && Component.KnownModloaders.TryGetValue(c.GetId(), out var entry))
            {
                result.Add(entry.Type);
            }
        }

        // TODO upstream: remove this or add version condition once Quilt drops official Fabric support
        if (result.Contains(ModLoaderType.Quilt) && !result.Contains(ModLoaderType.Fabric))
        {
            result.Add(ModLoaderType.Fabric);
        }

        if (GetComponentVersion("net.minecraft") == "1.20.1" && result.Contains(ModLoaderType.NeoForge) &&
            !result.Contains(ModLoaderType.Forge))
        {
            result.Add(ModLoaderType.Forge);
        }

        return result;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PackProfile.PackConfigOutJson))]
[JsonSerializable(typeof(PackProfile.PackConfigJson))]
internal sealed partial class PackConfigJsonContext : JsonSerializerContext;
