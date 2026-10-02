// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaInstallList.{h,cpp}: a list of discovered JavaInstall entries plus
// the JavaListLoadTask that probes every candidate path with a JavaChecker (fanned out through
// a ConcurrentTask) and feeds the valid ones into the list.
//
// Deviations from the C++ original:
//  - The BaseVersionList/QAbstractListModel surface (data() roles, VersionPointerRole, ...)
//    is UI plumbing and is not ported. RecommendedRole upstream is a constant `false` in this
//    revision (m_current_recommended is initialised to NULL and never assigned), so there is
//    no recommendation logic to port; consumers that need a default pick the newest entry.
//  - The APPLICATION->settings()->get("NumberOfConcurrentTasks") lookup becomes the
//    maxConcurrent constructor parameter (the host wires the settings value).
//  - The JavaListLoadTask subscribes to the ConcurrentTask Task::finished signal, which fires
//    on success AND failure; the port runs the same collection step unconditionally after the
//    job reaches its terminal state.
using FlintLauncher.Core.Tasks;

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
    private readonly int _maxConcurrent;
    private readonly string? _javaRoot;
    private Status _status = Status.NotDone;
    private JavaListLoadTask? _loadTask;
    private List<JavaInstall> _vlist = [];

    /// <summary><paramref name="javaRoot"/> is the launcher-managed java dir (the JavaDir
    /// setting under E2 store semantics — the host passes the resolved root; the default
    /// keeps the C++ cwd-relative behavior for bare fixtures).</summary>
    public JavaInstallList(bool onlyManagedVersions = false, int maxConcurrent = ConcurrentTask.DefaultMaxConcurrent,
        string? javaRoot = null)
    {
        _onlyManagedVersions = onlyManagedVersions;
        _maxConcurrent = maxConcurrent;
        _javaRoot = javaRoot;
    }

    /// <summary>Port of JavaInstallList::getLoadTask().</summary>
    public FlintTask GetLoadTask(bool forceReload = false)
    {
        Load();
        return GetCurrentTask()!;
    }

    private FlintTask? GetCurrentTask()
    {
        return _status == Status.InProgress ? _loadTask : null;
    }

    private void Load()
    {
        if (_status != Status.InProgress)
        {
            _status = Status.InProgress;
            _loadTask = new JavaListLoadTask(this, _onlyManagedVersions, _maxConcurrent, _javaRoot);
            // C++ starts the task immediately; the returned Task tracks the in-flight run.
            _ = _loadTask.StartAsync();
        }
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

/// <summary>Port of JavaListLoadTask.</summary>
public sealed class JavaListLoadTask : FlintTask
{
    private readonly JavaInstallList _list;
    private readonly bool _onlyManagedVersions;
    private readonly int _maxConcurrent;
    private readonly string? _javaRoot;
    private readonly List<JavaChecker.Result> _results = [];

    public JavaListLoadTask(JavaInstallList vlist, bool onlyManagedVersions = false,
        int maxConcurrent = ConcurrentTask.DefaultMaxConcurrent, string? javaRoot = null)
    {
        _list = vlist;
        _onlyManagedVersions = onlyManagedVersions;
        _maxConcurrent = maxConcurrent;
        _javaRoot = javaRoot;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Detecting Java installations...");

        var candidatePaths = _onlyManagedVersions
            ? JavaUtils.GetPrismJavaBundle(_javaRoot)
            : JavaUtils.FindJavaPaths(_javaRoot);

        var job = new ConcurrentTask("Java detection", _maxConcurrent);
        job.Progress += (_, progress) => EmitProgress(progress);

        // qDebug() << "Probing the following Java paths: ";
        var id = 0;
        foreach (var candidate in candidatePaths)
        {
            var checker = new JavaChecker(candidate, "", 0, 0, 0, id);
            checker.CheckFinished += result => _results.Add(result);
            job.AddTask(checker);
            id++;
        }

        await job.StartAsync(cancellationToken).ConfigureAwait(false);

        // the C++ connects Task::finished (fires on success and failure alike)
        JavaCheckerFinished();
    }

    private void JavaCheckerFinished()
    {
        _results.Sort((a, b) => a.Id.CompareTo(b.Id));

        // qDebug() << "Found the following valid Java installations:";
        var candidates = new List<JavaInstall>();
        foreach (var result in _results)
        {
            if (result.Validity == JavaChecker.Validity.Valid)
            {
                var javaVersion = new JavaInstall(result.JavaVersion, result.RealPlatform, result.Path)
                {
                    Is64Bit = result.Is64Bit,
                };
                candidates.Add(javaVersion);
            }
        }

        _list.UpdateListData(candidates);
        EmitSucceeded();
    }
}
