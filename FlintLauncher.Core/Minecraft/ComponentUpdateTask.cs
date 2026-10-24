// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/ComponentUpdateTask.{h,cpp} (ComponentUpdateTask_p.h inlined):
// loads the components of a component list AND resolves dependency issues between them.
//
// The C++ signal-driven "one shot async task" (see the FIXMEs at the top of the original)
// becomes a hand-rolled async state machine: the remote metadata loads run concurrently and
// their completion is awaited where the C++ waited for signals. Everything that manipulates
// the PackProfile (trivial removals, dependency insertion, update actions) follows the C++
// algorithm step by step; the resolution outcomes match the original semantics:
//   - Launch mode or offline mode => checkOnly pass (no components may be added/changed).
//   - Resolution mode (online)    => missing dependencies are inserted before their first
//     dependee, outdated dependency-only components are version-bumped, then re-resolved.
//
// Other deviations:
//  - The Connection* bookkeeping of ComponentUpdateTask_p.h becomes plain awaited Tasks; the
//    per-task finished/succeeded flags (and the upstream oddity that remoteLoadSucceeded also
//    records succeeded == false, keyed off the overall flag instead) are kept.
//  - The blocking Index::getLoadedVersion()/VersionList::waitToLoad() event-loop waits inside
//    performUpdateActions become awaits of the same load tasks.
//  - The `#if 0` Launch-vs-Resolution load branching of loadComponents() is compiled out
//    upstream as well; both modes load component versions.

using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Minecraft;

public sealed class ComponentUpdateTask(ComponentUpdateTask.Mode mode, NetMode netmode, PackProfile list) : FlintTask
{
    public enum Mode
    {
        Launch,
        Resolution,
    }

    private enum LoadResult
    {
        LoadedLocal = 0,
        RequiresRemote = 1,
        Failed = 2,
    }

    /// <summary>Port of the RemoteLoadStatus struct from ComponentUpdateTask_p.h (the
    /// RemoteLoadType enum is not ported: the ported flow only ever issues whole-version
    /// loads, so the C++ Index/List arms and the always-true dispatch had no producer).</summary>
    private sealed class RemoteLoadStatus
    {
        public int PackProfileIndex;

        public bool Finished;

        public bool Succeeded;

        public required FlintTask Task { get; init; }
    }

    /// <summary>Port of the RequireEx helper from ComponentUpdateTask.cpp.</summary>
    private sealed record RequireEx
    {
        public required string Uid { get; init; }

        public string EqualsVersion { get; init; } = "";

        public string Suggests { get; init; } = "";

        public int IndexOfFirstDependee { get; init; }
    }

    private static readonly Comparer<RequireEx> RequireComparer =
        Comparer<RequireEx>.Create((a, b) => string.CompareOrdinal(a.Uid, b.Uid));

    private readonly List<RemoteLoadStatus> _remoteLoadStatusList = [];

    private bool _remoteLoadSuccessful = true;

    private int _remoteTasksInProgress;

    public NetMode GetNetMode() => netmode;

    public override void Abort()
    {
        // the C++ canAbort()/abort() forward to the in-flight remote load tasks
        foreach (var status in _remoteLoadStatusList)
        {
            status.Task.Abort();
        }

        base.Abort();
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // "Loading components"
        await LoadComponentsAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- loading

    private LoadResult LoadComponent(Component component, out FlintTask? loadTask,
        NetMode netmode, CancellationToken cancellationToken)
    {
        loadTask = null;
        if (component.Loaded)
        {
            return LoadResult.LoadedLocal;
        }

        LoadResult result;
        var customPatchFilename = component.GetFilename();
        if (customPatchFilename is { } patchFile && patchFile.ExistsFile())
        {
            // if local file exists...

            // check for uid problems inside...
            var fileChanged = false;
            var file = ProfileUtils.ParseJsonFile(patchFile.Value, requireOrder: false);
            if (file.Uid != component.Uid)
            {
                file.Uid = component.Uid;
                fileChanged = true;
            }

            if (fileChanged)
            {
                // FIXME upstream @QUALITY: do not ignore return value
                ProfileUtils.SaveJsonFile(OneSixVersionFormat.VersionFileToJson(file), patchFile.Value);
            }

            component.File = file;
            component.Loaded = true;
            result = LoadResult.LoadedLocal;
        }
        else
        {
            var metaVersion = list.MetadataIndex.Get(component.Uid, component.Version);
            component.MetaVersion = metaVersion;
            if (metaVersion.IsLoaded)
            {
                component.Loaded = true;
                result = LoadResult.LoadedLocal;
            }
            else
            {
                loadTask = list.MetadataIndex.LoadVersion(component.Uid, component.Version, netmode);
                // the C++ start()s the task here and waits for its signals below
                _ = loadTask.StartAsync(cancellationToken);
                if (netmode == NetMode.Online)
                {
                    result = LoadResult.RequiresRemote;
                }
                else if (metaVersion.IsLoaded)
                {
                    result = LoadResult.LoadedLocal;
                }
                else
                {
                    result = LoadResult.Failed;
                }
            }
        }

        return result;
    }

    private static LoadResult ComposeLoadResult(LoadResult a, LoadResult b)
    {
        return a < b ? b : a;
    }

    private async Task LoadComponentsAsync(CancellationToken cancellationToken)
    {
        var result = LoadResult.LoadedLocal;
        var taskIndex = 0;
        var componentIndex = 0;
        _remoteLoadSuccessful = true;

        var waitTasks = new List<Task>();

        // load all the components OR their lists...
        foreach (var component in list.Components)
        {
            component.ResetComponentProblems();
            // FIXME upstream: to do this right, the version lists would be loaded first and the
            // versions decided during dependency resolution; the Launch/Resolution split below
            // is disabled upstream (#if 0) as well.
            var singleResult = LoadComponent(component, out var loadTask, netmode, cancellationToken);
            if (singleResult == LoadResult.LoadedLocal)
            {
                component.UpdateCachedData();
            }

            result = ComposeLoadResult(result, singleResult);
            if (loadTask is not null)
            {
                var status = new RemoteLoadStatus
                {
                    PackProfileIndex = componentIndex,
                    Task = loadTask,
                };
                _remoteLoadStatusList.Add(status);
                waitTasks.Add(AwaitRemoteLoadAsync(status));
                taskIndex++;
            }

            componentIndex++;
        }

        _remoteTasksInProgress = taskIndex;
        SetProgress(0, taskIndex);
        switch (result)
        {
            case LoadResult.LoadedLocal:
            {
                // Everything got loaded. Advance to dependency resolution.
                await PerformUpdateActionsAsync(cancellationToken);
                await ResolveDependenciesAsync(mode == Mode.Launch || netmode == NetMode.Offline, cancellationToken);
                return;
            }
            case LoadResult.RequiresRemote:
            {
                // we wait for the remote load completions (C++: signals)
                EmitDetails($"Downloading metadata for {taskIndex} components");
                await Task.WhenAll(waitTasks);
                await CheckIfAllFinishedAsync(cancellationToken);
                return;
            }
            case LoadResult.Failed:
            {
                EmitFailed("Some component metadata load tasks failed.");
                return;
            }
        }
    }

    /// <summary>
    /// Waits for one remote load started by LoadComponent and records the outcome (C++: the
    /// remoteLoadSucceeded/remoteLoadFailed signal handlers).
    /// </summary>
    private async Task AwaitRemoteLoadAsync(RemoteLoadStatus status)
    {
        var loadTask = status.Task;
        // the task is already running; StartAsync returns the in-flight run
        await loadTask.StartAsync();
        if (status.Finished)
        {
            // "Got multiple results from remote load task" guard; nothing to record
            return;
        }

        if (loadTask.State == TaskState.Succeeded)
        {
            RemoteLoadFinished(status, loadSucceeded: true);
        }
        else if (loadTask.State == TaskState.AbortedByUser)
        {
            RemoteLoadFinished(status, loadSucceeded: false);
        }
        else
        {
            RemoteLoadFinished(status, loadSucceeded: false);
        }
    }

    private void RemoteLoadFinished(RemoteLoadStatus status, bool loadSucceeded)
    {
        if (loadSucceeded)
        {
            // NOTE upstream oddity kept: the success path also stores succeeded == false; the
            // error report in CheckIfAllFinishedAsync keys off the overall flag instead
            status.Succeeded = false;
        }
        else
        {
            _remoteLoadSuccessful = false;
            status.Succeeded = false;
        }

        status.Finished = true;
        _remoteTasksInProgress--;

        // update the cached data of the component from the downloaded version file.
        if (loadSucceeded)
        {
            var component = list.GetComponent(status.PackProfileIndex);
            if (component is not null)
            {
                component.Loaded = true;
                component.UpdateCachedData();
            }
        }
    }

    private async Task CheckIfAllFinishedAsync(CancellationToken cancellationToken)
    {
        SetProgress(CurrentProgress + 1, TotalProgress);
        if (_remoteTasksInProgress != 0)
        {
            // not yet...
            return;
        }

        if (_remoteLoadSuccessful)
        {
            // nothing bad happened... clear the temp load status and proceed with looking at dependencies
            _remoteLoadStatusList.Clear();
            await PerformUpdateActionsAsync(cancellationToken);
            await ResolveDependenciesAsync(mode == Mode.Launch, cancellationToken);
        }
        else
        {
            // remote load failed... report error and bail
            var allErrorsList = new List<string>();
            foreach (var item in _remoteLoadStatusList)
            {
                if (!item.Succeeded)
                {
                    var component = list.GetComponent(item.PackProfileIndex);
                    allErrorsList.Add(
                        $"Could not download metadata for {component?.GetName()} {component?.Version}. Please change the version or try again later.");
                }
            }

            _remoteLoadStatusList.Clear();

            EmitFailed("Component metadata update task failed while downloading from remote server:\n"
                       + string.Join("\n", allErrorsList));
        }
    }

    // ---------------------------------------------------------------- dependency resolution

    private static (bool Ok, RequireEx Outcome) ComposeRequirement(RequireEx a, RequireEx b)
    {
        var outcome = new RequireEx
        {
            Uid = a.Uid,
            IndexOfFirstDependee = Math.Min(a.IndexOfFirstDependee, b.IndexOfFirstDependee),
        };
        if (a.EqualsVersion.Length == 0)
        {
            outcome = outcome with { EqualsVersion = b.EqualsVersion };
        }
        else if (b.EqualsVersion.Length == 0)
        {
            outcome = outcome with { EqualsVersion = a.EqualsVersion };
        }
        else if (a.EqualsVersion == b.EqualsVersion)
        {
            outcome = outcome with { EqualsVersion = a.EqualsVersion };
        }
        else
        {
            // FIXME upstream: mark error as explicit version conflict
            return (false, outcome);
        }

        if (a.Suggests.Length == 0)
        {
            outcome = outcome with { Suggests = b.Suggests };
        }
        else if (b.Suggests.Length == 0)
        {
            outcome = outcome with { Suggests = a.Suggests };
        }
        else
        {
            var aVer = new McVersion(a.Suggests);
            var bVer = new McVersion(b.Suggests);
            outcome = outcome with { Suggests = aVer < bVer ? b.Suggests : a.Suggests };
        }

        return (true, outcome);
    }

    /// <summary>gather the requirements from all components, finding any obvious conflicts</summary>
    private static bool GatherRequirementsFromComponents(IReadOnlyList<Component> input, SortedSet<RequireEx> output)
    {
        var succeeded = true;
        var componentNum = 0;
        foreach (var component in input)
        {
            foreach (var componentRequire in component.CachedRequires)
            {
                var found = output.FirstOrDefault(req => req.Uid == componentRequire.Uid);

                var componentRequireEx = new RequireEx
                {
                    Uid = componentRequire.Uid,
                    Suggests = componentRequire.Suggests,
                    EqualsVersion = componentRequire.EqualsVersion,
                    IndexOfFirstDependee = componentNum,
                };

                if (found is not null)
                {
                    // found... process it further
                    var (ok, outcome) = ComposeRequirement(componentRequireEx, found);
                    if (ok)
                    {
                        output.Remove(found);
                        output.Add(outcome);
                    }
                    else
                    {
                        succeeded = false;
                    }
                }
                else
                {
                    // not found, accumulate
                    output.Add(componentRequireEx);
                }
            }

            componentNum++;
        }

        return succeeded;
    }

    /// <summary>
    /// Get list of uids that can be trivially removed because nothing is depending on them
    /// anymore (and they are installed as deps).
    /// </summary>
    private static void GetTrivialRemovals(IReadOnlyList<Component> components, SortedSet<RequireEx> reqs,
        List<string> toRemove)
    {
        foreach (var component in components)
        {
            if (!component.DependencyOnly)
            {
                continue;
            }

            if (!component.CachedVolatile)
            {
                continue;
            }

            if (!reqs.Any(req => req.Uid == component.Uid))
            {
                toRemove.Add(component.Uid);
            }
        }
    }

    /// <summary>
    /// handles: trivial addition (there is an unmet requirement that can be met by adding
    /// something) and trivial version conflicts of dependencies (explicit version required and
    /// installed is different).
    /// </summary>
    private static bool GetTrivialComponentChanges(IReadOnlyDictionary<string, Component> index,
        SortedSet<RequireEx> input, SortedSet<RequireEx> toAdd,
        SortedSet<RequireEx> toChange)
    {
        var succeeded = true;
        // list the composed requirements and say if they are met or unmet
        foreach (var req in input)
        {
            if (req.EqualsVersion.Length == 0)
            {
                if (index.ContainsKey(req.Uid))
                {
                    // Met
                }
                else
                {
                    toAdd.Add(req); // Missing
                }
            }
            else
            {
                if (!index.TryGetValue(req.Uid, out var comp))
                {
                    toAdd.Add(req); // Missing
                }
                else if (comp.GetVersion() != req.EqualsVersion)
                {
                    if (comp.IsCustom())
                    {
                        succeeded = false; // LockedVersionNotSame
                    }
                    else if (comp.DependencyOnly)
                    {
                        toChange.Add(req); // VersionNotSame
                    }
                    else
                    {
                        succeeded = false; // LockedVersionNotSame
                    }
                }
                // else: Met
            }
        }

        return succeeded;
    }

    /// <summary>collects components that are dependent on or dependencies of the component</summary>
    private List<Component> CollectTreeLinked(string uid)
    {
        var linked = new List<Component>();

        foreach (var comp in list.Components)
        {
            if (comp.CachedRequires.Any(req => req.Uid == uid))
            {
                linked.Add(comp);
            }
        }

        if (list.ComponentIndex.TryGetValue(uid, out var component))
        {
            component.UpdateCachedData();
            foreach (var dep in component.CachedRequires)
            {
                if (list.ComponentIndex.TryGetValue(dep.Uid, out var found))
                {
                    linked.Add(found);
                }
            }
        }

        return linked;
    }

    // FIXME upstream, TODO: decouple dependency resolution from loading
    // FIXME upstream: This works directly with the PackProfile internals. It shouldn't!
    // FIXME upstream: throw all this away and use a graph
    private async Task ResolveDependenciesAsync(bool checkOnly, CancellationToken cancellationToken)
    {
        /*
         * this is a naive dependency resolving algorithm. all it does is check for following
         * conditions and react in simple ways:
         * 1. There are conflicting dependencies on the same uid with different exact version numbers
         *    -> hard error
         * 2. A dependency has non-matching exact version number
         *    -> hard error
         * 3. A dependency is entirely missing and needs to be injected before the dependee(s)
         *    -> requirements are injected
         *
         * NOTE upstream: this is a placeholder and should eventually be replaced with something 'serious'
         */
        var components = list.Components;
        var componentIndex = list.ComponentIndex;

        var allRequires = new SortedSet<RequireEx>(RequireComparer);
        var toRemove = new List<string>();
        do
        {
            allRequires.Clear();
            toRemove.Clear();
            if (!GatherRequirementsFromComponents(components, allRequires))
            {
                FinalizeComponents();
                EmitFailed("Conflicting requirements detected during dependency checking!");
                return;
            }

            GetTrivialRemovals(components, allRequires, toRemove);
            if (toRemove.Count != 0)
            {
                foreach (var remove in toRemove)
                {
                    _ = list.Remove(remove);
                }
            }
        } while (toRemove.Count != 0);

        var toAdd = new SortedSet<RequireEx>(RequireComparer);
        var toChange = new SortedSet<RequireEx>(RequireComparer);
        var succeeded = GetTrivialComponentChanges(componentIndex, allRequires, toAdd, toChange);
        if (!succeeded)
        {
            FinalizeComponents();
            EmitFailed("Instance has conflicting dependencies.");
            return;
        }

        if (checkOnly)
        {
            FinalizeComponents();
            if (toAdd.Count != 0 || toChange.Count != 0)
            {
                EmitFailed("Instance has unresolved dependencies while loading/checking for launch.");
            }
            else
            {
                EmitSucceeded();
            }

            return;
        }

        var recursionNeeded = false;
        if (toAdd.Count != 0)
        {
            // add stuff...
            foreach (var add in toAdd)
            {
                var component = new Component(list, add.Uid);
                if (add.EqualsVersion.Length != 0)
                {
                    // exact version
                    component.Version = add.EqualsVersion;
                }
                else
                {
                    // version needs to be decided
                    // ####################################################################
                    // HACK HACK HACK HACK FIXME upstream: this is a placeholder for deciding
                    // what version to use. For now, it is hardcoded.
                    if (add.Suggests.Length != 0)
                    {
                        component.Version = add.Suggests;
                    }
                    else
                    {
                        if (add.Uid == "org.lwjgl")
                        {
                            component.Version = "2.9.1";
                        }
                        else if (add.Uid == "org.lwjgl3")
                        {
                            component.Version = "3.1.2";
                        }
                        else if (add.Uid == "net.fabricmc.intermediary" || add.Uid == "org.quiltmc.hashed")
                        {
                            var minecraft = list.Components.FirstOrDefault(cmp => cmp.GetId() == "net.minecraft");
                            if (minecraft is not null)
                            {
                                component.Version = minecraft.GetVersion();
                            }
                        }
                    }
                    // HACK HACK HACK HACK FIXME upstream: this is a placeholder for deciding
                    // what version to use. For now, it is hardcoded.
                    // ####################################################################
                }

                component.DependencyOnly = true;
                // FIXME upstream: this should not work directly with the component list
                list.InsertComponent(add.IndexOfFirstDependee, component);
                // (the C++ also re-assigns componentIndex[add.uid]; InsertComponent did that already)
            }

            recursionNeeded = true;
        }

        if (toChange.Count != 0)
        {
            // change a version of something that exists
            foreach (var change in toChange)
            {
                if (componentIndex.TryGetValue(change.Uid, out var component))
                {
                    component.SetVersion(change.EqualsVersion);
                }
            }

            recursionNeeded = true;
        }

        if (recursionNeeded)
        {
            await LoadComponentsAsync(cancellationToken);
        }
        else
        {
            FinalizeComponents();
            EmitSucceeded();
        }
    }

    /// <summary>Variant visitation via lambda upstream; pattern matching here.</summary>
    private async Task PerformUpdateActionsAsync(CancellationToken cancellationToken)
    {
        bool addedActions;
        var toRemove = new List<string>();
        do
        {
            addedActions = false;
            toRemove.Clear();
            // the C++ range-for detaches (copies) the QList; iterate a snapshot so Remove() is safe
            foreach (var component in list.Components.ToArray())
            {
                var action = component.GetUpdateAction();
                switch (action)
                {
                    case UpdateAction.None:
                    {
                        // noop
                        break;
                    }
                    case UpdateAction.ChangeVersion cv:
                    {
                        component.SetVersion(cv.TargetVersion);
                        await component.WaitLoadMetaAsync(netmode, cancellationToken);
                        break;
                    }
                    case UpdateAction.LatestRecommendedCompatible lrc:
                    {
                        // (the C++ "no version list in metadata index" branch is unreachable:
                        // Index::get() creates the list entry)
                        var versionList = list.MetadataIndex.Get(component.GetId());
                        // port of the blocking VersionList::waitToLoad()
                        if (!versionList.IsLoaded)
                        {
                            await versionList.LoadTask(netmode).StartAsync(cancellationToken);
                        }

                        var recommended = versionList.GetRecommendedForParent(lrc.ParentUid, lrc.Version)
                                          ?? versionList.GetLatestForParent(lrc.ParentUid, lrc.Version);
                        if (recommended is not null)
                        {
                            component.SetVersion(recommended.Version);
                            await component.WaitLoadMetaAsync(netmode, cancellationToken);
                            break;
                        }

                        component.AddComponentProblem(ProblemSeverity.Error,
                            $"No compatible version of {component.GetName()} found for {lrc.ParentName} {lrc.Version}");
                        break;
                    }
                    case UpdateAction.Remove:
                    {
                        toRemove.Add(component.GetId());
                        break;
                    }
                    case UpdateAction.ImportantChanged ic:
                    {
                        // port of the blocking Index::getLoadedVersion()
                        await list.MetadataIndex.LoadVersion(component.GetId(), ic.OldVersion, netmode)
                            .StartAsync(cancellationToken);
                        var oldVersion = list.MetadataIndex.Get(component.GetId(), ic.OldVersion);
                        foreach (var oldReq in oldVersion.RequiredSet)
                        {
                            if (!component.CachedRequires.Contains(oldReq)
                                && list.ComponentIndex.TryGetValue(oldReq.Uid, out var oldReqComp))
                            {
                                oldReqComp.SetUpdateAction(new UpdateAction.Remove());
                                addedActions = true;
                            }
                        }

                        var linked = CollectTreeLinked(component.GetId());
                        foreach (var comp in linked)
                        {
                            if (comp.IsCustom())
                            {
                                continue;
                            }

                            var compUid = comp.GetId();
                            var parentReq = component.CachedRequires.FirstOrDefault(req => req.Uid == compUid);
                            if (parentReq is not null)
                            {
                                var newVersion = parentReq.EqualsVersion.Length == 0
                                    ? parentReq.Suggests
                                    : parentReq.EqualsVersion;
                                if (newVersion.Length != 0)
                                {
                                    comp.SetUpdateAction(new UpdateAction.ChangeVersion(newVersion));
                                }
                                else
                                {
                                    comp.SetUpdateAction(new UpdateAction.LatestRecommendedCompatible(
                                        component.GetId(), component.GetName(), component.GetVersion()));
                                }
                            }
                            else
                            {
                                comp.SetUpdateAction(new UpdateAction.LatestRecommendedCompatible(
                                    component.GetId(), component.GetName(), component.GetVersion()));
                            }

                            addedActions = true;
                        }

                        break;
                    }
                }

                component.ClearUpdateAction();
                foreach (var uid in toRemove)
                {
                    _ = list.Remove(uid);
                }
            }
        } while (addedActions);
    }

    private void FinalizeComponents()
    {
        var components = list.Components;
        var componentIndex = list.ComponentIndex;
        foreach (var component in components)
        {
            foreach (var req in component.CachedRequires)
            {
                if (!componentIndex.TryGetValue(req.Uid, out var reqComp))
                {
                    component.AddComponentProblem(ProblemSeverity.Error,
                        $"{component.GetName()} is missing requirement {req.Uid} {(req.EqualsVersion.Length == 0 ? req.Suggests : req.EqualsVersion)}");
                }
                else
                {
                    if (reqComp.GetProblems().Count != 0)
                    {
                        component.AddComponentProblem(reqComp.GetProblemSeverity(),
                            $"{reqComp.GetName()}, a dependency of this component, has reported issues");
                    }

                    if (req.EqualsVersion.Length != 0 && req.EqualsVersion != reqComp.GetVersion())
                    {
                        component.AddComponentProblem(ProblemSeverity.Error,
                            $"{reqComp.GetName()}, a dependency of this component, is not the required version {req.EqualsVersion}");
                    }
                    else if (req.Suggests.Length != 0 && req.Suggests != reqComp.GetVersion())
                    {
                        component.AddComponentProblem(ProblemSeverity.Warning,
                            $"{reqComp.GetName()}, a dependency of this component, is not the suggested version {req.Suggests}");
                    }
                }
            }

            foreach (var conflict in component.KnownConflictingComponents())
            {
                if (componentIndex.TryGetValue(conflict, out var foundComp))
                {
                    if (foundComp.IsCustom())
                    {
                        continue;
                    }

                    component.AddComponentProblem(ProblemSeverity.Warning,
                        $"{component.GetName()} and {foundComp.GetName()} are known to not work together. It is recommended to remove one of them.");
                }
            }
        }
    }
}
