// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the composition that drives a collection of work: SequenceAsync (strict order, first
// failure stops the chain) and RunAllAsync (the concurrency cap, sibling isolation, combined
// failure reasons). The 4096-item and concurrency-cap scenarios come from the C++
// Task_test.cpp checks.

using System.Diagnostics;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class TaskWorkTests
{
    // Counters for the concurrency-cap test. xUnit creates a fresh test class instance per test,
    // and locals captured by lambdas cannot be used with Interlocked.
    private int _runningUnits;
    private int _maxObservedUnits;

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 10000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.ElapsedMilliseconds < timeoutMilliseconds, "Timed out waiting for a condition.");
            await Task.Delay(10);
        }
    }

    private static Task Run(string name, ICollection<string> startOrder, bool succeeds)
    {
        lock (startOrder)
        {
            startOrder.Add(name);
        }

        return succeeds
            ? Task.CompletedTask
            : throw new InvalidOperationException($"failure of {name}");
    }

    // ---- SequenceAsync ----

    [Fact]
    public async Task SequenceAsyncRunsTheItemsInOrder()
    {
        var startOrder = new List<string>();

        await TaskWork.SequenceAsync(
            ["a", "b", "c"],
            (name, _) => Run(name, startOrder, succeeds: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "a", "b", "c" }, startOrder);
    }

    [Fact]
    public async Task SequenceAsyncFirstFailureStopsTheChain()
    {
        var startOrder = new List<string>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TaskWork.SequenceAsync(
            ["ok", "bad", "never"],
            (name, _) => Run(name, startOrder, succeeds: name != "bad"),
            TestContext.Current.CancellationToken));

        Assert.Equal("failure of bad", ex.Message);
        Assert.Equal(new[] { "ok", "bad" }, startOrder);
    }

    // ---- RunAllAsync ----

    [Fact]
    public async Task RunAllAsyncRunsEveryItemAndAggregatesTheFailures()
    {
        var startOrder = new List<string>();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => TaskWork.RunAllAsync(
            ["unit 1", "ok", "unit 2"],
            (name, _) => Run(name, startOrder, succeeds: name == "ok"),
            maxConcurrent: 2, TestContext.Current.CancellationToken));

        // A failing item neither stops nor cancels its siblings.
        Assert.Equal(new[] { "ok", "unit 1", "unit 2" }, startOrder.OrderBy(name => name));
        Assert.Equal(new[] { "failure of unit 1", "failure of unit 2" },
            ex.InnerExceptions.Select(failure => failure.Message).OrderBy(reason => reason).ToArray());
    }

    [Fact]
    public async Task RunAllAsyncOfOneFailureCarriesThatException()
    {
        var boom = new InvalidOperationException("boom");
        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            TaskWork.RunAllAsync([true, false],
                (succeeds, _) => succeeds ? Task.CompletedTask : Task.FromException(boom),
                maxConcurrent: 1, TestContext.Current.CancellationToken));

        Assert.Same(boom, Assert.Single(ex.InnerExceptions));
    }

    [Fact]
    public async Task RunAllAsyncKeepsASelfCancelledItemAsAFailure()
    {
        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            TaskWork.RunAllAsync([0], (_, _) => throw new OperationCanceledException("its own token"),
                maxConcurrent: 1, TestContext.Current.CancellationToken));

        // The batch token was not cancelled, so the item stopping on its own is a failure.
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(ex.InnerExceptions));
    }

    [Fact]
    public async Task ReasonOfListsTheAggregatedReasonsOnePerLine()
    {
        var ex = await Assert.ThrowsAsync<AggregateException>(() => TaskWork.RunAllAsync(
            ["unit 1", "unit 2"],
            (name, _) => Run(name, new List<string>(), succeeds: false),
            maxConcurrent: 1, TestContext.Current.CancellationToken));

        // AggregateException.Message would render "One or more errors occurred. (…) (…)".
        Assert.Equal(new[] { "failure of unit 1", "failure of unit 2" },
            TaskWork.ReasonOf(ex).Split('\n').OrderBy(reason => reason).ToArray());
        Assert.Equal("boom", TaskWork.ReasonOf(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task RunAllAsyncRespectsMaxConcurrent()
    {
        const int maxConcurrent = 2;
        var allReleased = new TaskCompletionSource();

        async Task HoldAsync(int _, CancellationToken token)
        {
            var running = Interlocked.Increment(ref _runningUnits);
            var currentMax = Volatile.Read(ref _maxObservedUnits);
            while (running > currentMax)
            {
                var prev = Interlocked.CompareExchange(ref _maxObservedUnits, running, currentMax);
                if (prev == currentMax)
                {
                    break;
                }

                currentMax = prev;
            }

            await allReleased.Task.WaitAsync(token);
            Interlocked.Decrement(ref _runningUnits);
        }

        var run = TaskWork.RunAllAsync([0, 1, 2, 3, 4], HoldAsync, maxConcurrent,
            TestContext.Current.CancellationToken);

        // The cap is actually reached (items do run in parallel up to it).
        await WaitForAsync(() => Volatile.Read(ref _runningUnits) == maxConcurrent);

        allReleased.SetResult();
        await run;

        // ... and never exceeded.
        Assert.Equal(maxConcurrent, Volatile.Read(ref _maxObservedUnits));
    }

    /// <summary>
    /// Port of test_stackOverflowInConcurrentTask: 4096 items must complete without overflowing
    /// the stack, even though every one of them finishes synchronously.
    /// </summary>
    [Fact]
    public async Task RunAllAsyncOfManySynchronousItemsDoesNotOverflowTheStack()
    {
        const int numItems = 1 << 12;
        var items = Enumerable.Range(0, numItems).ToArray();

        var run = TaskWork.RunAllAsync(items, (_, _) => Task.CompletedTask, maxConcurrent: 6,
            TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(run, Task.Delay(10_000));

        Assert.Same(run, completed);
        await run;
    }

    [Fact]
    public async Task RunAllAsyncCancellationSurfacesAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();

        var run = TaskWork.RunAllAsync([0, 1, 2], async (index, token) =>
        {
            if (index != 1)
            {
                return;
            }

            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, maxConcurrent: 1, cts.Token);

        await started.Task;
        await cts.CancelAsync();

        // The batch waits for the running items before it surfaces the cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
