// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.Core.Tasks;

/// <summary>Composition of work over a collection: the caller collects the items and one
/// closure that runs an item. An item that reports progress captures its own
/// <see cref="IProgress{T}"/>; nothing here channels it.</summary>
public static class TaskWork
{
    /// <summary>
    /// Runs the items one after another; the first exception stops the sequence and is
    /// propagated unchanged, so the remaining items never start.
    /// </summary>
    public static async Task SequenceAsync<TSource>(IEnumerable<TSource> items,
        Func<TSource, CancellationToken, Task> run, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await run(item, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs every item, at most <paramref name="maxConcurrent"/> at a time, and then throws an
    /// <see cref="AggregateException"/> carrying one entry per failed item — in no particular
    /// order, since it is the completion order. A failing item neither stops nor cancels its
    /// siblings; cancelling <paramref name="cancellationToken"/> stops the items that have not
    /// started, waits for the running ones, and surfaces as a cancellation of the batch.
    /// <para>Parallel.ForEachAsync stops handing out items after the first throw out of the body,
    /// so the body collects the failure instead: the whole batch runs and only then reports, and
    /// a failing download does not suppress its siblings' work.</para>
    /// </summary>
    public static async Task RunAllAsync<TSource>(IEnumerable<TSource> items,
        Func<TSource, CancellationToken, Task> run, int maxConcurrent,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);

        var failures = new List<Exception>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxConcurrent,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(items, options, async (item, token) =>
        {
            try
            {
                await run(item, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The batch is being aborted; Parallel.ForEachAsync surfaces the cancellation.
            }
            catch (Exception ex)
            {
                // An item that stops on its own is a failure like any other.
                Record(failures, ex);
            }
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (failures.Count > 0)
        {
            throw new AggregateException([.. failures]);
        }
    }

    private static void Record(List<Exception> failures, Exception failure)
    {
        lock (failures)
        {
            failures.Add(failure);
        }
    }

    /// <summary>
    /// The reason to present for a caught failure. A batch failure has no single message, so its
    /// entries' reasons are listed one per line instead of the <c>"&lt;summary&gt; (…) (…)"</c>
    /// text <see cref="AggregateException.Message"/> renders; the entries themselves stay
    /// available through <see cref="AggregateException.InnerExceptions"/>.
    /// </summary>
    public static string ReasonOf(Exception exception)
    {
        if (exception is not AggregateException aggregate)
        {
            return exception.Message;
        }

        return aggregate.InnerExceptions.Count == 1
            ? aggregate.InnerExceptions[0].Message
            : string.Join("\n", aggregate.InnerExceptions.Select(inner => inner.Message));
    }
}
