// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the launch step engine (LaunchTask/LaunchStep/TaskStepWrapper): step order,
// failure propagation, abort semantics, the proceed gate, substituteVariables and the
// LaunchCommand step (real /bin/echo sub-process, LogPrePostOutput of a real command).

using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Launch.Steps;
using Microsoft.Extensions.Logging;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class StepEngineTests
{
    private sealed class RecordingStep(
        LaunchTask parent,
        string name,
        List<string>? order = null,
        bool fail = false,
        Action? onFinalize = null)
        : LaunchStep(parent)
    {
        public string Name { get; } = name;

        internal override Task RunAsync(CancellationToken cancellationToken)
        {
            order?.Add(Name);
            return fail
                ? Task.FromException(new InvalidOperationException("step failed: " + Name))
                : Task.CompletedTask;
        }

        public override void FinalizeStep() => onFinalize?.Invoke();
    }

    /// <summary>A step that only finishes when released (or aborted).</summary>
    private sealed class WaitingStep(LaunchTask parent) : LaunchStep(parent)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started { get; private set; }

        internal override async Task RunAsync(CancellationToken cancellationToken)
        {
            Started = true;
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public override bool CanAbort() => true;

        public override bool AbortStep()
        {
            // the release ends the step; the engine's own token carries the abort
            Release.TrySetResult();
            return true;
        }
    }

    [Fact]
    public async Task Steps_RunInOrder_AndSucceed()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var order = new List<string>();
        var task = fixture.CreateTask(instance);
        task.AppendStep(new RecordingStep(task, "one", order));
        task.AppendStep(new RecordingStep(task, "two", order));
        task.AppendStep(new RecordingStep(task, "three", order));

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["one", "two", "three"], order);
        Assert.True(outcome.Succeeded, outcome.FailReason);
        Assert.False(instance.HasCrashed);
    }

    [Fact]
    public async Task Failure_StopsTheChain_AndPropagatesReason()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var order = new List<string>();
        var task = fixture.CreateTask(instance);
        task.AppendStep(new RecordingStep(task, "one", order));
        task.AppendStep(new RecordingStep(task, "boom", order, fail: true));
        task.AppendStep(new RecordingStep(task, "never", order));

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["one", "boom"], order); // the remaining step never runs
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Aborted);
        Assert.Equal("step failed: boom", outcome.FailReason);
        Assert.True(instance.HasCrashed); // emitFailed marks the instance as crashed
        Assert.False(instance.IsRunning);
    }

    [Fact]
    public async Task Finalize_RunsInReverseOrder()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var finalized = new List<string>();
        var task = fixture.CreateTask(instance);

        task.AppendStep(new RecordingStep(task, "one", [], onFinalize: () => finalized.Add("one")));
        task.AppendStep(new RecordingStep(task, "two", [], onFinalize: () => finalized.Add("two")));

        await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["two", "one"], finalized); // finalize from the current step backwards
    }

    [Fact]
    public async Task Abort_WhileStepRunning_FailsTheLaunchWithAbortedReason()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        var waiting = new WaitingStep(task);
        task.AppendStep(waiting);

        LaunchTask.LaunchOutcome outcome;
        var run = task.RunToCompletionAsync(TestContext.Current.CancellationToken);
        // wait until the step is running, then abort
        while (!waiting.Started)
        {
            await Task.Delay(10);
        }

        Assert.True(task.CanAbort());
        task.Abort();

        outcome = await run;

        // an aborted launch is its own outcome, not a failure
        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Aborted);
    }

    [Fact]
    public async Task Abort_BeforeStart_TerminalAborted()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        task.AppendStep(new RecordingStep(task, "never", []));

        task.Abort(); // before start: the C++ emits aborted() straight away
        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Aborted);
    }

    [Fact]
    public async Task ProceedGate_ReadyForLaunch_ThenProceed()
    {
        // attach first: the logger factory is injected at construction, so the task must be
        // built with the sink's factory to observe the gate edges
        var sink = FlintLogTest.Attach();
        try
        {
            using var fixture = new LaunchFixture();
            var instance = await fixture.CreateInstance();
            var task = fixture.CreateTask(instance, loggerFactory: FlintLogTest.Factory);
            var gate = new GatedStep(task);
            task.AppendStep(gate);

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task.ReadyForLaunch += () => ready.TrySetResult();

            LaunchTask.LaunchOutcome outcome;
            var run = task.RunToCompletionAsync(TestContext.Current.CancellationToken);
            await ready.Task; // the launch preparations are done

            Assert.Equal(LaunchTask.LaunchState.Waiting, task.CurrentState);
            Assert.True(task.CanAbort());

            task.Proceed();
            outcome = await run;

            Assert.True(outcome.Succeeded, outcome.FailReason);
            Assert.True(gate.Proceeded);
            // the opened gate must stop reporting "waiting" (the API turns a duplicate proceed
            // into an explicit 409 instead of a silent no-op)
            Assert.False(task.IsWaitingForProceed);
            // and both gate edges are visible in the diagnostics channel
            var launchCategory = LogCategories.Launch;
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

    private sealed class GatedStep(LaunchTask parent) : LaunchStep(parent)
    {
        public TaskCompletionSource ProceedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Proceeded;

        internal override async Task RunAsync(CancellationToken cancellationToken)
        {
            EmitReadyForLaunch();
            await ProceedTask.Task.ConfigureAwait(false);
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
        var success = fixture.CreateTask(instance);
        success.RequestProgress += _ => success.Proceed();
        success.AppendStep(new TaskStepWrapper(success, _ => NoopUnit()));
        var successOutcome = await success.RunToCompletionAsync(TestContext.Current.CancellationToken);
        Assert.True(successOutcome.Succeeded, successOutcome.FailReason);

        var failure = fixture.CreateTask(instance);
        failure.RequestProgress += _ => failure.Proceed();
        failure.AppendStep(new TaskStepWrapper(failure, _ => FailingUnit("no libraries today")));
        var failureOutcome = await failure.RunToCompletionAsync(TestContext.Current.CancellationToken);
        Assert.False(failureOutcome.Succeeded);
        Assert.False(failureOutcome.Aborted);
        Assert.Contains("Instance update failed because: no libraries today", failureOutcome.FailReason);
    }

    [Fact]
    public async Task TaskStepWrapper_StopsAcceptingAbortsOnceTheBodyReturned()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        var bodyReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new TaskStepWrapper(task, async _ =>
        {
            bodyReturned.TrySetResult();
            await release.Task;
        });

        task.RequestProgress += _ => task.Proceed();
        task.AppendStep(wrapper);

        LaunchTask.LaunchOutcome outcome;
        var run = task.RunToCompletionAsync(TestContext.Current.CancellationToken);
        await bodyReturned.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(wrapper.CanAbort());

        release.TrySetResult();
        outcome = await run;
        Assert.True(outcome.Succeeded, outcome.FailReason);

        // a body that returned has nothing left to stop
        Assert.False(wrapper.CanAbort());
    }

    private static Task NoopUnit() =>
        Task.CompletedTask;

    private static Task FailingUnit(string reason) =>
        throw new InvalidOperationException(reason);

    [Fact]
    public async Task SubstituteVariables_UsesLaunchEnvironment_WhenRequested()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);

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
        instance.GameRoot.CreateDirectory(); // normally created by CreateGameFolders
        var task = fixture.CreateTask(instance);
        var step = new LaunchCommand(task, "/bin/echo hello-from-echo", "Pre-Load");
        step.SetWorkingDirectory(instance.GameRoot);
        task.AppendStep(step);

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailReason);
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Line.Contains("Running Pre-Load command: /bin/echo hello-from-echo"));
        // The game-output pipeline stores plain lines as Unknown after the level-guessing
        // heuristics find no pattern (LogParser::guessLevel) — same as the C++.
        Assert.Contains(lines, e => e is { Level: MessageLevel.Unknown, Line: "hello-from-echo" });
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
        var task = fixture.CreateTask(instance);
        task.AppendStep(new LaunchCommand(task, "/bin/sh -c 'echo boom >&2; exit 3'", "Pre-Load"));

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Aborted);
        Assert.Contains("Pre-Load command failed with code 3", outcome.FailReason);
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e is { Level: MessageLevel.Unknown, Line: "boom" });
    }

    [Fact]
    public async Task LaunchCommand_EmptyCommand_Fails()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        task.AppendStep(new LaunchCommand(task, "", "Pre-Load"));

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Aborted);
        Assert.Contains("Pre-Load command is empty, skipping.", outcome.FailReason);
    }

    [Fact]
    public async Task TextPrint_EmitsItsLines()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        task.AppendStep(new TextPrint(task, "hello\nworld\n", MessageLevel.Launcher));

        await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e is { Level: MessageLevel.Launcher, Line: "hello\nworld\n" });
    }

    // ------------------------------------------------------------------ staged execution

    [Fact]
    public async Task Stage_StartsItsStepsTogether_AndTheNextStageWaits()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var marks = new List<string>();

        var a = new HoldingStep(task, () =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }
        }, release.Task, () => { });
        var b = new HoldingStep(task, () =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }
        }, release.Task, () => { });
        var after = new RecordingStep(task, "after", marks);

        task.AppendStage(a, b);
        task.AppendStep(after);

        LaunchTask.LaunchOutcome outcome;
        var run = task.RunToCompletionAsync(TestContext.Current.CancellationToken);
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        // both siblings are in flight and the following stage has not started
        Assert.Empty(marks);

        release.TrySetResult();
        outcome = await run;

        Assert.True(outcome.Succeeded, outcome.FailReason);
        Assert.Equal(["after"], marks);
    }

    [Fact]
    public async Task Stage_RunsEverySibling_EvenWhenOneFails()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        var ran = new List<string>();
        var boom = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        task.AppendStage(
            new HoldingStep(task, () => { }, boom.Task, () => ran.Add("ok")),
            new FailingAfterSignalStep(task, boom));
        task.AppendStep(new RecordingStep(task, "never", ran));

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Aborted);
        Assert.Contains("boom", outcome.FailReason);
        Assert.Contains("ok", ran);          // the sibling ran to its own end
        Assert.DoesNotContain("never", ran); // the next stage never started
    }

    [Fact]
    public async Task Abort_WhileAStepRuns_StopsTheChainAndReportsAborted()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        var ran = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holding = new AbortableHoldingStep(task, started, release.Task, () => ran.Add("first"));
        task.AppendStep(holding);
        task.AppendStep(new RecordingStep(task, "never", ran));

        LaunchTask.LaunchOutcome outcome;
        var run = task.RunToCompletionAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(task.CanAbort());
        task.Abort();
        outcome = await run;

        Assert.Equal(["first"], ran);
        Assert.DoesNotContain("never", ran);
        Assert.True(outcome.Aborted);
    }

    [Fact]
    public async Task ThrowingFinalizer_DoesNotSkipTheEarlierFinalizers()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = fixture.CreateTask(instance);
        var finalized = new List<string>();

        task.AppendStep(new ThrowingFinalizeStep(task));
        task.AppendStep(new RecordingStep(task, "second", [], onFinalize: () => finalized.Add("second")));

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        // the deeper step ran first and threw; the shallower one still finalized
        Assert.Equal(["second"], finalized);
        Assert.True(outcome.Succeeded, outcome.FailReason);
    }

    /// <summary>Emits success only when released, so a stage can be observed mid-flight.</summary>
    private sealed class HoldingStep(
        LaunchTask parent,
        Action onStart,
        Task release,
        Action onFinish) : LaunchStep(parent)
    {
        internal override async Task RunAsync(CancellationToken cancellationToken)
        {
            onStart();
            await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            onFinish();
        }

        public override bool CanAbort() => true;
    }

    private sealed class FailingAfterSignalStep(LaunchTask parent, TaskCompletionSource signal) : LaunchStep(parent)
    {
        internal override async Task RunAsync(CancellationToken cancellationToken)
        {
            signal.TrySetResult();
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
    }

    /// <summary>Parks until released; an abort ends the wait through the step's own signal, so
    /// the body unwinds the way a real step's does.</summary>
    private sealed class AbortableHoldingStep(
        LaunchTask parent,
        TaskCompletionSource started,
        Task release,
        Action onFinish) : LaunchStep(parent)
    {
        private readonly TaskCompletionSource _aborted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal override async Task RunAsync(CancellationToken cancellationToken)
        {
            onFinish();
            started.TrySetResult();
            await Task.WhenAny(release, _aborted.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public override bool CanAbort() => true;

        public override bool AbortStep()
        {
            // the step decides how it stops: a real step either kills its process or writes
            // the abort command
            _aborted.TrySetResult();
            return true;
        }
    }

    private sealed class ThrowingFinalizeStep(LaunchTask parent) : LaunchStep(parent)
    {
        internal override Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void FinalizeStep() => throw new InvalidOperationException("cleanup failed");
    }
}
