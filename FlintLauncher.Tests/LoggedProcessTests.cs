// SPDX-License-Identifier: GPL-3.0-only
// Tests for the LoggedProcess port: line-based stdout/stderr capture (including interleaved
// output), state transitions, exit codes, kill/abort, stdin writes and working directory
// handling. Unix-only fixtures (/bin/sh + coreutils); skipped trivially on other hosts.
using FlintLauncher.Core.Launch;
using Xunit;

namespace FlintLauncher.Tests;

public class LoggedProcessTests
{
    private static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private sealed class Collector
    {
        public List<(MessageLevel Level, string Line)> Lines { get; } = [];

        public List<LoggedProcessState> States { get; } = [];

        public void Attach(LoggedProcess process)
        {
            process.Log += (lines, level) => Lines.AddRange(lines.Select(line => (level, line)));
            process.StateChanged += state => States.Add(state);
        }

        public List<string> LinesOf(MessageLevel level) =>
            Lines.Where(l => l.Level == level).Select(l => l.Line).ToList();
    }

    private static async Task<LoggedProcess> RunAsync(LoggedProcess process, string program, params string[] args)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.StateChanged += state =>
        {
            if (state is LoggedProcessState.Finished or LoggedProcessState.Crashed or LoggedProcessState.Aborted
                or LoggedProcessState.FailedToStart)
            {
                completion.TrySetResult();
            }
        };
        Assert.True(process.Start(program, args));
        await completion.Task;
        // belt and braces: WaitForExitAsync awaits the terminal-state source, so the state and
        // exit code are always settled for the caller's assertions (event edge under load).
        await process.WaitForExitAsync();
        return process;
    }

    [Fact]
    public async Task CapturesStdoutLines()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);

        // printf emits two separate lines (echo would join its arguments with spaces)
        await RunAsync(process, "/usr/bin/printf", "line one\nline two\n");

        Assert.Equal(["line one", "line two"], collector.LinesOf(MessageLevel.StdOut));
        Assert.Equal(["Process exited with code 0."], collector.LinesOf(MessageLevel.Launcher));
        Assert.Equal(LoggedProcessState.Finished, process.State);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task CapturesInterleavedStdoutAndStderr()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);

        await RunAsync(process, "/bin/sh", "-c",
            "echo out1; echo err1 >&2; echo out2; echo err2 >&2; echo out3");

        Assert.Equal(["out1", "out2", "out3"], collector.LinesOf(MessageLevel.StdOut));
        Assert.Equal(["err1", "err2"], collector.LinesOf(MessageLevel.StdErr));
    }

    [Fact]
    public async Task CarriageReturnsAreStripped()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);

        // the C++ reprocess() removes carriage returns before splitting on line feeds (CR is
        // NOT a line separator); /usr/bin/printf interprets the \r escape (/bin/echo would
        // pass it through literally)
        await RunAsync(process, "/usr/bin/printf", "progress\rdone\n");

        Assert.Equal(["progressdone"], collector.LinesOf(MessageLevel.StdOut));
    }

    [Fact]
    public async Task NonZeroExitCode_IsReportedAsFinished()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);

        await RunAsync(process, "/bin/sh", "-c", "exit 3");

        Assert.Equal(LoggedProcessState.Finished, process.State);
        Assert.Equal(3, process.ExitCode);
        Assert.Contains(collector.LinesOf(MessageLevel.Launcher), l => l.Contains("Process exited with code 3."));
    }

    [Fact]
    public async Task Kill_ReportsAborted()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.StateChanged += state =>
        {
            if (state is LoggedProcessState.Aborted or LoggedProcessState.Finished or LoggedProcessState.Crashed)
            {
                completion.TrySetResult();
            }
        };
        Assert.True(process.Start("/bin/sh", ["-c", "sleep 30"]));
        await Task.Delay(200); // let it reach Running
        Assert.Equal(LoggedProcessState.Running, process.State);

        process.Kill();
        await completion.Task;

        Assert.Equal(LoggedProcessState.Aborted, process.State);
        Assert.Contains(collector.LinesOf(MessageLevel.Error), l => l.Contains("Process was killed by user."));
    }

    [Fact]
    public async Task Write_SendsToStdin()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);

        // read one line from stdin, echo it back and exit (so the process finishes on its own)
        var completion = RunAsync(process, "/bin/sh", "-c", "read line; echo \"$line\"");
        await Task.Delay(200); // let the shell block on read
        process.Write("hello stdin\n");
        await completion;

        Assert.Equal(["hello stdin"], collector.LinesOf(MessageLevel.StdOut));
        Assert.Equal(LoggedProcessState.Finished, process.State);
    }

    [Fact]
    public async Task WorkingDirectory_IsApplied()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);
        var workDir = Path.Combine(Directory.GetCurrentDirectory(), $"flint-loggedproc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            process.SetWorkingDirectory(workDir);
            await RunAsync(process, "/bin/pwd");

            Assert.Equal([workDir], collector.LinesOf(MessageLevel.StdOut));
        }
        finally
        {
            Directory.Delete(workDir);
        }
    }

    [Fact]
    public async Task MissingProgram_ReportsFailedToStart()
    {
        if (!IsUnix)
        {
            return;
        }
        using var process = new LoggedProcess();
        var collector = new Collector();
        collector.Attach(process);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.StateChanged += state =>
        {
            if (state == LoggedProcessState.FailedToStart)
            {
                completion.TrySetResult();
            }
        };
        Assert.False(process.Start("/nonexistent/definitely-missing-binary", []));
        await completion.Task;

        Assert.Equal(LoggedProcessState.FailedToStart, process.State);
        Assert.Contains(collector.Lines, l => l.Line.Contains("The process failed to start"));
        Assert.Equal(MessageLevel.Fatal, collector.Lines.Single(l => l.Line.Contains("The process failed to start")).Level);
        Assert.NotEqual("", process.ErrorString);
    }
}
