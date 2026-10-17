// SPDX-License-Identifier: GPL-3.0-only
// Tests for the launch step engine (LaunchTask/LaunchStep/TaskStepWrapper): step order,
// failure propagation, abort semantics, the proceed gate, substituteVariables and the
// LaunchCommand step (real /bin/echo sub-process, LogPrePostOutput of a real command).

using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlintLauncher.Tests;

public class StepEngineTests
{
    private sealed class RecordingStep : LaunchStep
    {
        private readonly List<string>? _order;
        private readonly bool _fail;
        private readonly Action? _onFinalize;

        public RecordingStep(LaunchTask parent, string name, List<string>? order = null, bool fail = false,
                             Action? onFinalize = null) : base(parent)
        {
            Name = name;
            _order = order;
            _fail = fail;
            _onFinalize = onFinalize;
        }

        public string Name { get; }

        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _order?.Add(Name);
            if (_fail)
            {
                EmitFailed("step failed: " + Name);
            }
            else
            {
                EmitSucceeded();
            }
            return Task.CompletedTask;
        }

        public override void FinalizeStep() => _onFinalize?.Invoke();
    }

    /// <summary>A step that only finishes when released (or aborted).</summary>
    private sealed class WaitingStep : LaunchStep
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WaitingStep(LaunchTask parent, string name) : base(parent)
        {
            Name = name;
        }

        public string Name { get; }

        public bool Started { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Started = true;
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            EmitSucceeded();
        }

        public override bool CanAbort() => true;

        public override bool AbortStep()
        {
            // the default cooperative cancellation routes to the AbortedByUser state
            Abort();
            return true;
        }
    }

    [Fact]
    public async Task Steps_RunInOrder_AndSucceed()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var order = new List<string>();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new RecordingStep(task, "one", order));
        task.AppendStep(new RecordingStep(task, "two", order));
        task.AppendStep(new RecordingStep(task, "three", order));

        await task.StartAsync();

        Assert.Equal(["one", "two", "three"], order);
        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.False(instance.HasCrashed);
    }

    [Fact]
    public async Task Failure_StopsTheChain_AndPropagatesReason()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var order = new List<string>();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new RecordingStep(task, "one", order));
        task.AppendStep(new RecordingStep(task, "boom", order, fail: true));
        task.AppendStep(new RecordingStep(task, "never", order));

        await task.StartAsync();

        Assert.Equal(["one", "boom"], order); // the remaining step never runs
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("step failed: boom", task.FailReason);
        Assert.True(instance.HasCrashed); // emitFailed marks the instance as crashed
        Assert.False(instance.IsRunning);
    }

    [Fact]
    public async Task Finalize_RunsInReverseOrder()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var finalized = new List<string>();
        var task = LaunchTask.Create(instance, fixture.CreateServices());

        task.AppendStep(new RecordingStep(task, "one", [], onFinalize: () => finalized.Add("one")));
        task.AppendStep(new RecordingStep(task, "two", [], onFinalize: () => finalized.Add("two")));

        await task.StartAsync();

        Assert.Equal(["two", "one"], finalized); // finalize from the current step backwards
    }

    [Fact]
    public async Task Abort_WhileStepRunning_FailsTheLaunchWithAbortedReason()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        var waiting = new WaitingStep(task, "waiting");
        task.AppendStep(waiting);

        var run = task.StartAsync();
        // wait until the step is running, then abort
        while (!waiting.Started)
        {
            await Task.Delay(10);
        }
        Assert.True(task.CanAbort());
        task.Abort();

        await run;

        // C++ semantics: the aborted step reports finished -> finalizeSteps(false) ->
        // emitFailed("Aborted"); the LaunchState::Aborted flag is transient.
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("Aborted", task.FailReason);
    }

    [Fact]
    public async Task Abort_BeforeStart_TerminalAborted()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new RecordingStep(task, "never", []));

        task.Abort(); // before start: the C++ emits aborted() straight away
        await task.StartAsync();

        Assert.Equal(TaskState.AbortedByUser, task.State);
    }

    [Fact]
    public async Task ProceedGate_ReadyForLaunch_ThenProceed()
    {
        // attach first: the logger factory is injected at construction (LaunchServices), so
        // the task must be built with the sink's factory to observe the gate edges
        var sink = FlintLogTest.Attach();
        try
        {
            using var fixture = new LaunchFixture();
            var instance = await fixture.CreateInstance();
            var task = LaunchTask.Create(instance,
                fixture.CreateServices(loggerFactory: FlintLogTest.Factory));
            var gate = new GatedStep(task);
            task.AppendStep(gate);

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task.ReadyForLaunch += () => ready.TrySetResult();

            var run = task.StartAsync();
            await ready.Task; // the launch preparations are done

            Assert.Equal(LaunchTask.LaunchState.Waiting, task.CurrentState);
            Assert.True(task.CanAbort());

            task.Proceed();
            await run;

            Assert.Equal(TaskState.Succeeded, task.State);
            Assert.True(gate.Proceeded);
            // the opened gate must stop reporting "waiting" (the API turns a duplicate proceed
            // into an explicit 409 instead of a silent no-op)
            Assert.False(task.IsWaitingForProceed);
            // and both gate edges are visible in the diagnostics channel
            var launchCategory = Core.Diagnostics.LogCategories.Launch;
            Assert.True(sink.Has(LogLevel.Information, launchCategory, "ReadyForLaunch gate open"),
                "gate opening should be logged");
            Assert.True(sink.Has(LogLevel.Information, launchCategory, "Proceed routed to step GatedStep"),
                "proceed consumption should be logged");
        }
        finally
        {
            FlintLogTest.Detach();
        }
    }

    private sealed class GatedStep : LaunchStep
    {
        public TaskCompletionSource ProceedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Proceeded;

        public GatedStep(LaunchTask parent) : base(parent)
        {
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            EmitReadyForLaunch();
            await ProceedTask.Task.ConfigureAwait(false);
            EmitSucceeded();
        }

        public override void Proceed()
        {
            Proceeded = true;
            ProceedTask.TrySetResult();
        }

        public override bool CanAbort() => true;
    }

    [Fact]
    public async Task TaskStepWrapper_SucceedsAndFailsWithTheWrappedTask()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();

        // TaskStepWrapper gates on progressReportingRequest; the controller answers with
        // proceed() — mirror the LaunchController.OnProgressRequested behavior here.
        var success = LaunchTask.Create(instance, fixture.CreateServices());
        success.RequestProgress += _ => success.Proceed();
        success.AppendStep(new TaskStepWrapper(success, new NoopTask()));
        await success.StartAsync();
        Assert.Equal(TaskState.Succeeded, success.State);

        var failure = LaunchTask.Create(instance, fixture.CreateServices());
        failure.RequestProgress += _ => failure.Proceed();
        failure.AppendStep(new TaskStepWrapper(failure, new FailingTask("no libraries today")));
        await failure.StartAsync();
        Assert.Equal(TaskState.Failed, failure.State);
        Assert.Contains("Instance update failed because: no libraries today", failure.FailReason);
    }

    private sealed class NoopTask : FlintTask
    {
        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }
    }

    private sealed class FailingTask(string reason) : FlintTask
    {
        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            EmitFailed(reason);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SubstituteVariables_UsesLaunchEnvironment_WhenRequested()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());

        // The launch environment builds on the base one: INST_* vars exist in both.
        var baseArgs = task.SubstituteVariables("${INST_ID}");
        var launchArgs = task.SubstituteVariables("${INST_ID}", isLaunch: true);
        Assert.Equal(instance.Id(), Assert.Single(baseArgs));
        Assert.Equal(instance.Id(), Assert.Single(launchArgs));
    }

    [Fact]
    public async Task LaunchCommand_RunsEcho_AndCapturesOutput()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return; // /bin/echo is a unix fixture
        }
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        Directory.CreateDirectory(instance.GameRoot()); // normally created by CreateGameFolders
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        var step = new LaunchCommand(task, "/bin/echo hello-from-echo", "Pre-Load");
        step.SetWorkingDirectory(instance.GameRoot());
        task.AppendStep(step);

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Line.Contains("Running Pre-Load command: /bin/echo hello-from-echo"));
        // The game-output pipeline stores plain lines as Unknown after the level-guessing
        // heuristics find no pattern (LogParser::guessLevel) — same as the C++.
        Assert.Contains(lines, e => e.Level == MessageLevel.Unknown && e.Line == "hello-from-echo");
        Assert.Contains(lines, e => e.Line.Contains("Pre-Load command ran successfully"));
    }

    [Fact]
    public async Task LaunchCommand_Fails_WhenExitCodeNonZero()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new LaunchCommand(task, "/bin/sh -c 'echo boom >&2; exit 3'", "Pre-Load"));

        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Pre-Load command failed with code 3", task.FailReason);
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Level == MessageLevel.Unknown && e.Line == "boom");
    }

    [Fact]
    public async Task LaunchCommand_EmptyCommand_Fails()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new LaunchCommand(task, "", "Pre-Load"));

        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Pre-Load command is empty, skipping.", task.FailReason);
    }

    [Fact]
    public async Task TextPrint_EmitsItsLines()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new TextPrint(task, "hello\nworld\n", MessageLevel.Launcher));

        await task.StartAsync();

        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Level == MessageLevel.Launcher && e.Line == "hello\nworld\n");
    }
}
