// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/JavaCommon.{h,cpp} (the pure logic; the Qt dialogs are UI-layer and left for
// the UI phase). CheckJvmArgs is the "JVM args sanity" check. The TestCheck two-pass flow died
// with the JavaCheck.jar retirement: it had no production consumer in the headless core.
using System.Text.RegularExpressions;

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
}
