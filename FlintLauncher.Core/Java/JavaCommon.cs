// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/JavaCommon.{h,cpp} (the pure logic; the Qt dialogs are UI-layer and left for
// the UI phase). CheckJvmArgs is the "JVM args sanity" check; TestCheck ports the two-pass
// Java test flow (a bare run, then a run with the user's arguments) with the dialog outcomes
// replaced by an outcome enum the UI can map to its messages.
using System.Text.RegularExpressions;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Java;

public static partial class JavaCommon
{
    [GeneratedRegex("-Xm[sx]")]
    private static partial Regex MemRegex();

    [GeneratedRegex("-version:.*")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// Port of JavaCommon::checkJVMArgs: rejects manually set memory options and required
    /// version flags. The dialog texts checkJVMArgs would show are available through
    /// <see cref="JvmArgsWarning"/>; this method stays the pure predicate.
    /// </summary>
    public static bool CheckJvmArgs(string jvmargs) => JvmArgsWarning(jvmargs) is null;

    /// <summary>
    /// The warning text (QObject::tr strings) the C++ dialogs would show for bad arguments, or
    /// null when the arguments are acceptable.
    /// </summary>
    public static string? JvmArgsWarning(string jvmargs)
    {
        if (jvmargs.Contains("-XX:PermSize=", StringComparison.Ordinal) || MemRegex().IsMatch(jvmargs) || jvmargs.Contains("-XX-MaxHeapSize", StringComparison.Ordinal) ||
            jvmargs.Contains("-XX:InitialHeapSize", StringComparison.Ordinal))
        {
            return "You tried to manually set a JVM memory option (using \"-XX:PermSize\", \"-XX-MaxHeapSize\", \"-XX:InitialHeapSize\", \"-Xmx\" " +
                   "or \"-Xms\").\n" +
                   "There are dedicated boxes for these in the settings (Java tab, in the Memory group at the top).\n" +
                   "This message will be displayed until you remove them from the JVM arguments.";
        }
        // block lunacy with passing required version to the JVM
        if (VersionRegex().IsMatch(jvmargs))
        {
            return "You tried to pass required Java version argument to the JVM (using \"-version:xxx\"). This is not safe and will not be " +
                   "allowed.\n" +
                   "This message will be displayed until you remove this from the JVM arguments.";
        }
        return null;
    }

    /// <summary>The outcome of <see cref="TestCheck.RunAsync"/> (each maps to one of the C++ dialog helpers).</summary>
    public enum TestOutcome
    {
        /// <summary>checkJVMArgs rejected the arguments before anything ran (javaArgsWereBad is never reached).</summary>
        ArgsRejected,

        /// <summary>javaCheckNotFound: the JavaCheck.jar is missing.</summary>
        CheckerNotFound,

        /// <summary>javaBinaryWasBad: the bare check failed.</summary>
        BinaryBad,

        /// <summary>javaArgsWereBad: the check with the user's arguments failed.</summary>
        ArgsBad,

        /// <summary>javaWasOk.</summary>
        Ok,
    }

    /// <summary>Port of JavaCommon::TestCheck (minus the QWidget parentage and dialogs).
    /// The upstream permGen slot is gone with the Java <8 support floor: PermSize only
    /// ever mattered on those runtimes, so the second pass carries memory args only.</summary>
    public sealed class TestCheck
    {
        public async Task<TestOutcome> RunAsync(string path, string args, int minMem, int maxMem,
                                                CancellationToken cancellationToken = default)
        {
            if (!CheckJvmArgs(args))
            {
                return TestOutcome.ArgsRejected;
            }
            if (JavaUtils.GetJavaCheckPath().Length == 0)
            {
                return TestOutcome.CheckerNotFound;
            }

            var first = await CheckOnce(new JavaChecker(path, "", 0, 0), cancellationToken).ConfigureAwait(false);
            if (first is null || first.Validity != JavaChecker.Validity.Valid)
            {
                return TestOutcome.BinaryBad;
            }
            // NOTE: upstream passes maxMem into both slots here (new JavaChecker(m_path, m_args,
            // m_maxMem, m_maxMem, ...)); the quirk is preserved.
            var second = await CheckOnce(new JavaChecker(path, args, maxMem, maxMem), cancellationToken)
                .ConfigureAwait(false);
            return second is not null && second.Validity == JavaChecker.Validity.Valid
                ? TestOutcome.Ok
                : TestOutcome.ArgsBad;
        }

        private static async Task<JavaChecker.Result?> CheckOnce(JavaChecker checker, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<JavaChecker.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            checker.CheckFinished += result => completion.TrySetResult(result);
            await checker.StartAsync(cancellationToken).ConfigureAwait(false);
            return completion.Task.IsCompleted ? completion.Task.Result : null;
        }
    }
}
