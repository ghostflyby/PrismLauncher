// SPDX-License-Identifier: GPL-3.0-only
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

using FlintLauncher.Core.Tasks;
using JdkFind;

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
    private readonly string? _javaRoot;
    private Status _status = Status.NotDone;
    private JavaListLoadTask? _loadTask;
    private List<JavaInstall> _vlist = [];

    /// <summary><paramref name="javaRoot"/> is the launcher-managed java dir (the JavaDir
    /// setting under E2 store semantics — the host passes the resolved root; the default
    /// keeps the C++ cwd-relative behavior for bare fixtures).</summary>
    public JavaInstallList(bool onlyManagedVersions = false, string? javaRoot = null)
    {
        _onlyManagedVersions = onlyManagedVersions;
        _javaRoot = javaRoot;
    }

    /// <summary>Port of JavaInstallList::getLoadTask().</summary>
    public FlintTask GetLoadTask(bool forceReload = false) => Load();

    private FlintTask Load()
    {
        if (_status == Status.InProgress && _loadTask is { } inFlight)
        {
            return inFlight;
        }

        _status = Status.InProgress;
        var task = new JavaListLoadTask(this, _onlyManagedVersions, _javaRoot);
        _loadTask = task;
        // C++ starts the task immediately; the returned Task tracks the in-flight run.
        _ = task.StartAsync();
        return task;
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
        _loadTask = null;
    }

    /// <summary>
    /// Port of JavaInstallList::sortVersions() with the sortJavas predicate:
    /// comp(left, right) = right &gt; left (trichotomy: left &lt; right) — an ASCENDING order by
    /// JavaInstall::operator&lt; (architecture first, then version id, then path).
    /// </summary>
    public void SortVersions()
    {
        _vlist.Sort((left, right) => right.GreaterThan(left) ? -1 : left.GreaterThan(right) ? 1 : 0);
    }
}

/// <summary>Port of JavaListLoadTask. The detection itself lives in
/// <see cref="JavaDetection"/> (JdkFind LocateAsync primary + FromExecutable fallback); this
/// task is the list-model shell around it and maps the candidates onto
/// <see cref="JavaInstall"/>.</summary>
public sealed class JavaListLoadTask(
    JavaInstallList vlist,
    bool onlyManagedVersions = false,
    string? javaRoot = null,
    IReadOnlyList<IJvmProvider>? providers = null)
    : FlintTask
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Detecting Java installations...");

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
        EmitSucceeded();
    }
}
