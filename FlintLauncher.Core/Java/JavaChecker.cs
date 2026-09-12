// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaChecker.{h,cpp}: runs `java -jar JavaCheck.jar [jvm-args...]` and
// parses the key=value properties the checker prints (os.arch, java.version, java.vendor).
//
// Deviations from the C++ original:
//  - QProcess becomes System.Diagnostics.Process (AOT-safe) with a 15 s kill timer like the
//    C++ QTimer. A timeout kills the tree and reports the Errored validity (the C++ path goes
//    through QProcess::CrashExit, which .NET cannot distinguish from a non-zero exit code:
//    any non-zero exit maps to Errored — JavaCheck only ever exits 0/1).
//  - When the checker jar is missing, C++ executeTask() silently returns, leaving the task
//    (and any ConcurrentTask wrapping it) running forever; the port reports an Errored result
//    and succeeds so callers can react. Upstream UI pre-checks the jar path, so this is only
//    reachable when the jar vanished between the pre-check and the run.
//  - The Qt checkFinished signal becomes the CheckFinished event; like the C++, the task
//    always reaches the Succeeded state afterwards (the Result carries the outcome).
//  - Output is decoded as UTF-8 (QLocal8Bit is UTF-8 on macOS/Linux; the checker output is
//    ASCII in practice).
using System.Diagnostics;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Text;

namespace FlintLauncher.Core.Java;

public sealed class JavaChecker : FlintTask
{
    /// <summary>Port of JavaChecker::Result::Validity.</summary>
    public enum Validity
    {
        Errored,
        ReturnedInvalidData,
        Valid,
    }

    /// <summary>Port of JavaChecker::Result.</summary>
    public sealed class Result
    {
        public string Path { get; set; } = "";

        public int Id { get; set; }

        public string MojangPlatform { get; set; } = "";

        public string RealPlatform { get; set; } = "";

        public JavaVersion JavaVersion { get; set; } = new();

        public string JavaVendor { get; set; } = "";

        public string OutLog { get; set; } = "";

        public string ErrorLog { get; set; } = "";

        public bool Is64Bit { get; set; }

        public Validity Validity { get; set; } = Validity.Errored;
    }

    /// <summary>The check timeout (C++: m_killTimer.start(15000) — "NO MERCY. NO ABUSE.").</summary>
    public static readonly TimeSpan KillTimeout = TimeSpan.FromMilliseconds(15000);

    private readonly string _path;
    private readonly string _args;
    private readonly int _minMem;
    private readonly int _maxMem;
    private readonly int _permGen;
    private readonly int _id;

    public JavaChecker(string path, string args, int minMem = 0, int maxMem = 0, int permGen = 0, int id = 0)
    {
        _path = path;
        _args = args;
        _minMem = minMem;
        _maxMem = maxMem;
        _permGen = permGen;
        _id = id;
    }

    /// <summary>Port of the checkFinished(const Result&) signal.</summary>
    public event Action<Result>? CheckFinished;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var checkerJar = JavaUtils.GetJavaCheckPath();

        if (checkerJar.Length == 0)
        {
            // qDebug() << "Java checker library could not be found. Please check your installation.";
            EmitFinished(new Result
            {
                Path = _path,
                Id = _id,
                ErrorLog = "Java checker library could not be found. Please check your installation.",
                Validity = Validity.Errored,
            });
            return;
        }

        var args = new List<string>();
        if (_args.Length != 0)
        {
            args.AddRange(Commandline.SplitArgs(_args));
        }
        if (_minMem != 0)
        {
            args.Add($"-Xms{_minMem}m");
        }
        if (_maxMem != 0)
        {
            args.Add($"-Xmx{_maxMem}m");
        }
        if (_permGen != 64 && _permGen != 0)
        {
            args.Add($"-XX:PermSize={_permGen}m");
        }

        args.Add("-jar");
        args.Add(checkerJar);

        using var process = new Process();
        var startInfo = process.StartInfo;
        startInfo.FileName = _path;
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        var cleanEnv = JavaUtils.CleanEnvironment();
        startInfo.Environment.Clear();
        foreach (var (key, value) in cleanEnv.Vars)
        {
            startInfo.Environment[key] = value;
        }

        // qDebug() << "Running java checker:" << m_path << args.join(" ");
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // QProcess::FailedToStart path
            EmitFinished(new Result
            {
                Path = _path,
                Id = _id,
                ErrorLog = ex.Message,
                Validity = Validity.Errored,
            });
            return;
        }

        // read both pipes concurrently so a chatty child cannot deadlock the parent
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken).WaitAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken).WaitAsync(cancellationToken);

        // kill timer
        var exited = process.WaitForExitAsync(cancellationToken);
        var finishedFirst = await Task.WhenAny(exited, Task.Delay(KillTimeout, cancellationToken)).ConfigureAwait(false);
        if (finishedFirst != exited)
        {
            // Java checker has been killed by timeout. NO MERCY. NO ABUSE.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            var stdoutPartial = await SafeAwait(stdoutTask).ConfigureAwait(false);
            var stderrPartial = await SafeAwait(stderrTask).ConfigureAwait(false);
            EmitFinished(BuildResult(stdoutPartial, stderrPartial, errored: true));
            return;
        }

        // the exited task completed; cancellation (abort) surfaces as OperationCanceledException
        await exited.ConfigureAwait(false);
        var stdout = await SafeAwait(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeAwait(stderrTask).ConfigureAwait(false);

        var exitCode = process.ExitCode;
        if (exitCode != 0)
        {
            // C++: CrashExit or exit code 1 -> Errored
            EmitFinished(BuildResult(stdout, stderr, errored: true));
            return;
        }

        var result = BuildResult(stdout, stderr, errored: false);
        var results = ParseCheckOutput(stdout);
        if (!results.TryGetValue("os.arch", out var osArch) || !results.TryGetValue("java.version", out var javaVersion) ||
            !results.TryGetValue("java.vendor", out var javaVendor))
        {
            result.Validity = Validity.ReturnedInvalidData;
            EmitFinished(result);
            return;
        }

        var is64 = osArch is "x86_64" or "amd64" or "aarch64" or "arm64" or "riscv64" or "ppc64le" or "ppc64";

        result.Validity = Validity.Valid;
        result.Is64Bit = is64;
        result.MojangPlatform = is64 ? "64" : "32";
        result.RealPlatform = osArch;
        result.JavaVersion = new JavaVersion(javaVersion);
        result.JavaVendor = javaVendor;
        // Java checker succeeded.
        EmitFinished(result);
    }

    private Result BuildResult(string stdout, string stderr, bool errored)
    {
        var result = new Result
        {
            Path = _path,
            Id = _id,
            OutLog = stdout,
            ErrorLog = stderr,
        };
        if (errored)
        {
            result.Validity = Validity.Errored;
        }
        return result;
    }

    private void EmitFinished(Result result)
    {
        CheckFinished?.Invoke(result);
        EmitSucceeded();
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Port of the stdout parsing shared with JavaChecker::finished: trimmed lines of the form
    /// key=value into a map. Lines containing the bedrock strata mount point are skipped
    /// (GH-4125 workaround) and lines without exactly two non-empty parts are ignored.
    /// </summary>
    public static Dictionary<string, string> ParseCheckOutput(string stdout)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            // NOTE: workaround for GH-4125, where garbage is getting printed into stdout on bedrock linux
            if (line.Contains("/bedrock/strata"))
            {
                continue;
            }

            // QString::split('=', Qt::SkipEmptyParts): empty parts are dropped before the
            // exactly-two-parts check (so "a==b" parses as a=b and "a=" is ignored).
            var parts = line.Split('=');
            var nonEmpty = parts.Where(p => p.Length != 0).ToArray();
            if (nonEmpty.Length != 2)
            {
                continue;
            }
            results[nonEmpty[0]] = nonEmpty[1];
        }
        return results;
    }
}
