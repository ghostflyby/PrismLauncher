// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/Task_test.cpp, adapted to async xUnit (Task::start() -> await task.StartAsync()).
// Keeps the original scenarios (status/progress setters, basic/concurrent/sequential/multiple
// options runs, the 4096-subtask stack safety check) and adds coverage for the ConcurrentTask
// concurrency limit, the MultipleOptionsTask fallback order, composite failure semantics and
// abort.

using System.Diagnostics;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class TaskTests
{
    // Counters for the concurrency-limit test. xUnit creates a fresh test class instance per
    // test, so plain fields are safe. (Locals captured by lambdas cannot be used with Interlocked.)
    private int runningChildren;
    private int maxObservedChildren;
    private int startedCount;

    // ---- test doubles (the C++ test file defines BasicTask / BasicTask_MultiStep locally) ----

    /// <summary>Does nothing. Only used for testing. (C++ BasicTask: emits succeeded.)</summary>
    private sealed class BasicTestTask : FlintTask
    {
        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        // The C++ test calls the public slots setStatus()/setProgress() directly; the C# ports
        // of those are protected, so expose them.
        public void DoSetStatus(string status) => EmitStatus(status);

        public void DoSetProgress(long current, long total) => SetProgress(current, total);
    }

    /// <summary>Does nothing. Only used for testing. (C++ BasicTask_MultiStep.)</summary>
    private sealed class BasicMultiStepTestTask : FlintTask
    {
        public override bool IsMultiStep => true;

        // Mirrors BasicTask_MultiStep::executeTask(): never emits a terminal state.
        protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void DoSetStatus(string status) => EmitStatus(status);
    }

    /// <summary>Runs an arbitrary body, for tasks with custom timing.</summary>
    private sealed class CallbackTestTask : FlintTask
    {
        private readonly Func<CallbackTestTask, CancellationToken, Task> _body;

        public CallbackTestTask(Func<CallbackTestTask, CancellationToken, Task> body)
        {
            _body = body;
        }

        // Public shims for the protected Emit* methods, callable from test lambdas.
        public void Succeed() => EmitSucceeded();

        public void Fail(string reason) => EmitFailed(reason);

        protected override async Task ExecuteAsync(CancellationToken cancellationToken) =>
            await _body(this, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records its start order and succeeds or fails deterministically.</summary>
    private sealed class NamedTestTask : FlintTask
    {
        private readonly string _name;
        private readonly ICollection<string> _startOrder;
        private readonly bool _succeeds;

        public NamedTestTask(string name, ICollection<string> startOrder, bool succeeds)
        {
            _name = name;
            _startOrder = startOrder;
            _succeeds = succeeds;
        }

        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            lock (_startOrder)
            {
                _startOrder.Add(_name);
            }

            if (_succeeds)
            {
                EmitSucceeded();
            }
            else
            {
                EmitFailed($"failure of {_name}");
            }

            return Task.CompletedTask;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 10000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.ElapsedMilliseconds < timeoutMilliseconds, "Timed out waiting for a condition.");
            await Task.Delay(10);
        }
    }

    // ---- ports of the C++ TaskTest scenarios ----

    [Fact]
    public void TestSetStatusNoMultiStep()
    {
        var t = new BasicTestTask();
        var status = "test status";

        t.DoSetStatus(status);

        Assert.Equal(status, t.Status);
        // Task::getStepProgress() is empty unless a composite overrides it.
        Assert.Empty(t.GetStepProgress());
    }

    [Fact]
    public void TestSetStatusMultiStep()
    {
        var t = new BasicMultiStepTestTask();
        var status = "test status";

        t.DoSetStatus(status);

        Assert.Equal(status, t.Status);
        // Even though it is multi step, it does not override getStepProgress(), so it stays empty.
        Assert.Empty(t.GetStepProgress());
    }

    [Fact]
    public void TestSetProgress()
    {
        var t = new BasicTestTask();
        long current = 42;
        long total = 207;

        t.DoSetProgress(current, total);

        Assert.Equal(current, t.CurrentProgress);
        Assert.Equal(total, t.TotalProgress);
    }

    [Fact]
    public async Task TestBasicRun()
    {
        var t = new BasicTestTask();
        var succeededInHandler = false;
        t.Succeeded += task => succeededInHandler = task.WasSuccessful;

        await t.StartAsync();

        Assert.True(succeededInHandler, "Task finished but was not successful when it should have been.");
        Assert.True(t.WasSuccessful, "Task didn't finish as it should.");
        Assert.True(t.IsFinished);
    }

    [Fact]
    public async Task TestBasicConcurrentRun()
    {
        var t1 = new BasicTestTask();
        var t2 = new BasicTestTask();
        var t3 = new BasicTestTask();

        var t = new ConcurrentTask();
        t.AddTask(t1);
        t.AddTask(t2);
        t.AddTask(t3);

        await t.StartAsync();

        Assert.True(t.WasSuccessful, "Task finished but was not successful when it should have been.");
        Assert.True(t1.WasSuccessful);
        Assert.True(t2.WasSuccessful);
        Assert.True(t3.WasSuccessful);
    }

    /// <summary>Tests if starting new tasks after the 6 initial ones is working.</summary>
    [Fact]
    public async Task TestMoreConcurrentRun()
    {
        var subTasks = new[] { new BasicTestTask(), new BasicTestTask(), new BasicTestTask(), new BasicTestTask(), new BasicTestTask(), new BasicTestTask(), new BasicTestTask(), new BasicTestTask(), new BasicTestTask() };

        var t = new ConcurrentTask();
        foreach (var subTask in subTasks)
        {
            t.AddTask(subTask);
        }

        await t.StartAsync();

        Assert.True(t.WasSuccessful, "Task finished but was not successful when it should have been.");
        Assert.All(subTasks, subTask => Assert.True(subTask.WasSuccessful));
    }

    [Fact]
    public async Task TestBasicSequentialRun()
    {
        var t1 = new BasicTestTask();
        var t2 = new BasicTestTask();
        var t3 = new BasicTestTask();

        var t = new SequentialTask();
        t.AddTask(t1);
        t.AddTask(t2);
        t.AddTask(t3);

        await t.StartAsync();

        Assert.True(t.WasSuccessful, "Task finished but was not successful when it should have been.");
        Assert.True(t1.WasSuccessful);
        Assert.True(t2.WasSuccessful);
        Assert.True(t3.WasSuccessful);
    }

    [Fact]
    public async Task TestBasicMultipleOptionsRun()
    {
        var t1 = new BasicTestTask();
        var t2 = new BasicTestTask();
        var t3 = new BasicTestTask();

        var t = new MultipleOptionsTask();
        t.AddTask(t1);
        t.AddTask(t2);
        t.AddTask(t3);

        await t.StartAsync();

        Assert.True(t.WasSuccessful, "Task finished but was not successful when it should have been.");
        Assert.True(t1.WasSuccessful);
        Assert.False(t2.WasSuccessful);
        Assert.False(t3.WasSuccessful);
        // The remaining attempts were never started (they are not merely "not successful").
        Assert.Equal(TaskState.Inactive, t2.State);
        Assert.Equal(TaskState.Inactive, t3.State);
    }

    /// <summary>
    /// Port of test_stackOverflowInConcurrentTask: 4096 subtasks must complete without
    /// overflowing the stack (the C++ guards this with queued connections; here Task.Yield()
    /// between a subtask completing and the parent bookkeeping plays that role).
    /// </summary>
    [Fact]
    public async Task TestManySubtasksDoNotOverflowTheStack()
    {
        const int numTasks = 1 << 12;

        var bigTask = new ConcurrentTask();
        for (var i = 0; i < numTasks; i++)
        {
            bigTask.AddTask(new BasicTestTask());
        }

        var run = bigTask.StartAsync();
        var completed = await Task.WhenAny(run, Task.Delay(10_000));

        Assert.Same(run, completed);
        Assert.True(bigTask.WasSuccessful);
    }

    // ---- added coverage: concurrency limit, fallback order, composite failures, abort ----

    [Fact]
    public async Task ConcurrentTaskRespectsMaxConcurrentLimit()
    {
        const int maxConcurrent = 2;
        var allReleased = new TaskCompletionSource();
        var children = new List<FlintTask>();

        for (var i = 0; i < 5; i++)
        {
            children.Add(new CallbackTestTask(async (task, cancellationToken) =>
            {
                var running = Interlocked.Increment(ref runningChildren);
                var currentMax = Volatile.Read(ref maxObservedChildren);
                while (running > currentMax)
                {
                    var prev = Interlocked.CompareExchange(ref maxObservedChildren, running, currentMax);
                    if (prev == currentMax)
                    {
                        break;
                    }

                    currentMax = prev;
                }

                await allReleased.Task.WaitAsync(cancellationToken);

                Interlocked.Decrement(ref runningChildren);
                task.Succeed();
            }));
        }

        var t = new ConcurrentTask("", maxConcurrent);
        foreach (var child in children)
        {
            t.AddTask(child);
        }

        var run = t.StartAsync();

        // The limit is actually reached (subtasks do run in parallel up to the cap).
        await WaitForAsync(() => Volatile.Read(ref runningChildren) == maxConcurrent);

        allReleased.SetResult();
        await run;

        Assert.True(t.WasSuccessful);
        Assert.All(children, child => Assert.True(child.WasSuccessful));
        // ... and never exceeded.
        Assert.Equal(maxConcurrent, Volatile.Read(ref maxObservedChildren));
    }

    [Fact]
    public async Task ConcurrentTaskEmptySucceeds()
    {
        var t = new ConcurrentTask();
        await t.StartAsync();
        Assert.True(t.WasSuccessful);
    }

    [Fact]
    public async Task ConcurrentTaskChildFailureDoesNotAbortSiblings()
    {
        // Port of the C++ semantics: a failed subtask neither cancels its siblings nor fails the
        // parent until every subtask has been drained.
        var release = new TaskCompletionSource();
        var failing = new CallbackTestTask((task, _) =>
        {
            task.Fail("boom");
            return Task.CompletedTask;
        });
        var slowSibling = new CallbackTestTask(async (task, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            task.Succeed();
        });

        var t = new ConcurrentTask("", 2);
        t.AddTask(failing);
        t.AddTask(slowSibling);

        var run = t.StartAsync();

        // The sibling still got started even though the other subtask already failed.
        await WaitForAsync(() => slowSibling.State == TaskState.Running);

        release.SetResult();
        await run;

        Assert.Equal(TaskState.Failed, t.State);
        Assert.Equal("boom", t.FailReason);
        Assert.Equal(TaskState.Failed, failing.State);
        Assert.True(slowSibling.WasSuccessful);
    }

    [Fact]
    public async Task ConcurrentTaskCombinesFailureReasonsOfMultipleFailures()
    {
        var t = new ConcurrentTask();
        t.AddTask(new CallbackTestTask((task, _) =>
        {
            task.Fail("boom 1");
            return Task.CompletedTask;
        }));
        t.AddTask(new CallbackTestTask((task, _) =>
        {
            task.Fail("boom 2");
            return Task.CompletedTask;
        }));

        await t.StartAsync();

        Assert.Equal(TaskState.Failed, t.State);
        // The C++ joins the subtask reasons over a hash map, so the order is not meaningful.
        var lines = t.FailReason.Split('\n');
        Assert.Equal("Multiple subtasks failed", lines[0]);
        Assert.Equal(new[] { "boom 1", "boom 2" }, lines.Skip(1).OrderBy(reason => reason).ToArray());
    }

    [Fact]
    public async Task SequentialTaskFirstFailureFailsAndRemainingNeverStart()
    {
        var startOrder = new List<string>();
        var ok = new NamedTestTask("ok", startOrder, succeeds: true);
        var bad = new NamedTestTask("bad", startOrder, succeeds: false);
        var never = new NamedTestTask("never", startOrder, succeeds: true);

        var t = new SequentialTask();
        t.AddTask(ok);
        t.AddTask(bad);
        t.AddTask(never);

        await t.StartAsync();

        Assert.Equal(TaskState.Failed, t.State);
        Assert.Equal("failure of bad", t.FailReason);
        Assert.Equal(TaskState.Succeeded, ok.State);
        Assert.Equal(TaskState.Failed, bad.State);
        Assert.Equal(TaskState.Inactive, never.State);
        Assert.Equal(new[] { "ok", "bad" }, startOrder);
    }

    [Fact]
    public async Task SequentialTaskAggregatesProgressTotals()
    {
        var startOrder = new List<string>();
        var t = new SequentialTask();
        t.AddTask(new NamedTestTask("ok", startOrder, succeeds: true));
        t.AddTask(new NamedTestTask("bad", startOrder, succeeds: false));
        t.AddTask(new NamedTestTask("never", startOrder, succeeds: true));

        await t.StartAsync();

        // Port of SequentialTask::updateState(): progress is done subtasks out of the total size.
        var progress = t.GetProgress();
        Assert.Equal(2L, progress.Current);
        Assert.Equal(3L, progress.Total);
        Assert.Equal("Executing task 2 out of 3", t.Status);
    }

    [Fact]
    public async Task ConcurrentTaskAggregatesProgressTotals()
    {
        var t = new ConcurrentTask();
        t.AddTask(new BasicTestTask());
        t.AddTask(new BasicTestTask());
        t.AddTask(new BasicTestTask());

        await t.StartAsync();

        // Port of ConcurrentTask::updateState(): "done subtasks out of the total size".
        var progress = t.GetProgress();
        Assert.Equal(3L, progress.Current);
        Assert.Equal(3L, progress.Total);
        Assert.Equal(TaskStepState.Succeeded, progress.State);
        Assert.Equal("Executing 0 task(s) (3 out of 3 are done)", t.Status);
    }

    [Fact]
    public async Task MultipleOptionsTaskFallsThroughFailuresInOrder()
    {
        var startOrder = new List<string>();
        var fail1 = new NamedTestTask("fail1", startOrder, succeeds: false);
        var fail2 = new NamedTestTask("fail2", startOrder, succeeds: false);
        var ok = new NamedTestTask("ok", startOrder, succeeds: true);

        var t = new MultipleOptionsTask();
        t.AddTask(fail1);
        t.AddTask(fail2);
        t.AddTask(ok);

        await t.StartAsync();

        Assert.True(t.WasSuccessful);
        Assert.Equal(TaskState.Failed, fail1.State);
        Assert.Equal(TaskState.Failed, fail2.State);
        Assert.True(ok.WasSuccessful);
        // Attempts run one at a time, in order, until one succeeds.
        Assert.Equal(new[] { "fail1", "fail2", "ok" }, startOrder);
    }

    [Fact]
    public async Task MultipleOptionsTaskReportsFailureWhenAllAttemptsFail()
    {
        var startOrder = new List<string>();
        var t = new MultipleOptionsTask();
        t.AddTask(new NamedTestTask("a", startOrder, succeeds: false));
        t.AddTask(new NamedTestTask("b", startOrder, succeeds: false));
        t.AddTask(new NamedTestTask("c", startOrder, succeeds: false));

        await t.StartAsync();

        Assert.Equal(TaskState.Failed, t.State);
        // The C++ reports tr("All attempts have failed!"), not the subtasks' reasons.
        Assert.Equal("All attempts have failed!", t.FailReason);
        Assert.Equal(new[] { "a", "b", "c" }, startOrder);
        Assert.Equal(3L, t.GetProgress().Current);
        Assert.Equal(3L, t.GetProgress().Total);
    }

    [Fact]
    public async Task StartWhileRunningDoesNotRestart()
    {
        var release = new TaskCompletionSource();
        var t = new CallbackTestTask(async (task, _) =>
        {
            Interlocked.Increment(ref startedCount);
            await release.Task;
            task.Succeed();
        });

        var first = t.StartAsync();
        var second = t.StartAsync(); // C++ start() does nothing while the task is running

        await WaitForAsync(() => Volatile.Read(ref startedCount) == 1);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref startedCount));
        Assert.True(t.WasSuccessful);
    }

    [Fact]
    public async Task FinishedTaskCanBeRestarted()
    {
        var t = new BasicTestTask();

        await t.StartAsync();
        Assert.True(t.WasSuccessful);

        // Task::start() restarts from any end state.
        await t.StartAsync();
        Assert.True(t.WasSuccessful);
    }

    [Fact]
    public async Task AbortCancelsTaskThatObservesTheToken()
    {
        var started = new TaskCompletionSource();
        var t = new CallbackTestTask(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });

        var run = t.StartAsync();
        await started.Task;

        t.Abort();
        await run;

        Assert.Equal(TaskState.AbortedByUser, t.State);
        Assert.Equal("Aborted", t.FailReason);
        Assert.True(t.IsFinished);
    }

    [Fact]
    public async Task AbortAbortsCompositeAndItsSubTasks()
    {
        var childStarted = new TaskCompletionSource();
        var childAborted = new TaskCompletionSource();
        var child = new CallbackTestTask(async (_, cancellationToken) =>
        {
            childStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });
        child.Aborted += _ => childAborted.TrySetResult();
        var neverStarted = new BasicTestTask();

        var t = new SequentialTask();
        t.AddTask(child);
        t.AddTask(neverStarted);

        var run = t.StartAsync();
        await childStarted.Task;

        // ConcurrentTask::abort() finishes the parent right away and cancels the subtasks.
        t.Abort();
        await run;
        await childAborted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TaskState.AbortedByUser, t.State);
        Assert.Equal("Aborted", t.FailReason);
        Assert.Equal(TaskState.AbortedByUser, child.State);
        Assert.Equal(TaskState.Inactive, neverStarted.State);
    }
}
