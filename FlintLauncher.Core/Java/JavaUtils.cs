// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaUtils.{h,cpp} (JVM discovery + the process-environment cleaner).
//
// The C++ class is stateless, so the port flattens it into a static class. Discovery is
// layered (see JavaDetection for the pipeline):
//  - Primary — the JdkFind provider suite (NuGet JdkFind): validated installations
//    (release file + executable, plain / macOS-bundle / Homebrew-keg / JDK8 inner-JRE
//    layouts) from JAVA_HOME, PATH, the macOS JVM directories, /usr/lib/jvm and the other
//    unix distro locations, the Windows registry + Program Files vendor dirs, ~/.jdks,
//    SDKMAN, asdf, Gradle toolchains, Jabba, Scoop and the flatpak runtime extensions.
//    This closes the documented Windows-registry stub below.
//  - Fallback (this file) — the C++-parity filesystem scan, bounded by the Java 8 support
//    floor (2026-10-05 user decision; a documented deviation from upstream, which still
//    caters to Apple Java 6): the Apple-6-era candidates are pruned (JavaVM.framework,
//    the applet plugin, the Xcode itms bundle, /System/Library JVMs, the Contents/Commands
//    layout, the jre6/jre7 Windows paths), leaving Contents/Home bundles (incl. the JDK 8
//    inner JRE) and release-less installs in the directories and name filters JdkFind
//    deliberately does not model (Gentoo/AOSC, /usr/java, /usr/lib64/jvm, /opt/jdk*,
//    /opt/ibm, /app/jdk, $SNAP mirroring, FreeBSD/OpenBSD /usr/local). The tool-manager
//    scans (sdkman, asdf, gradle) stay in the fallback too — harmless overlap with
//    JdkFind, collapsed by the exact-string dedup in JavaDetection.
//  - Launcher-owned sources (this file) — the Minecraft launcher runtime bundle, the
//    launcher-managed java root, the FLINT_JAVA_PATHS environment override.
// The fallback and launcher-owned candidates flow through JavaDetection.DetectAsync, which
// checker-validates whatever the primary layer did not already cover.

using FlintLauncher.Core.Instances;
using TruePath;

namespace FlintLauncher.Core.Java;

public static class JavaUtils
{
    /// <summary>Port of JavaUtils::javaExecutable ("javaw.exe" on Windows, "java" elsewhere).</summary>
    public static string JavaExecutable => OperatingSystem.IsWindows() ? "javaw.exe" : "java";

    /// <summary>The os.arch spellings the launcher counts as 64-bit (shared by the detection
    /// pipeline, which derives JavaInstall.Is64Bit and the Mojang "32"/"64" platform string
    /// from release/probe architecture strings of this shape).</summary>
    public static bool Is64BitArchitecture(string osArch) =>
        osArch is "x86_64" or "amd64" or "aarch64" or "arm64" or "riscv64" or "ppc64le" or "ppc64";

    /// <summary>Port of JavaUtils::MakeJavaPtr.</summary>
    public static JavaInstall MakeJavaPtr(string path, string? id = "unknown", string? arch = "unknown")
    {
        return new JavaInstall(id ?? "unknown", arch ?? "unknown", path);
    }

    /// <summary>Port of JavaUtils::GetDefaultJava.</summary>
    public static JavaInstall GetDefaultJava()
    {
        return new JavaInstall("java", "unknown", OperatingSystem.IsWindows() ? "javaw" : "java");
    }

    // ------------------------------------------------------------- environment

    /// <summary>Port of stripVariableEntries (path-list difference, ':' or ';' separated).</summary>
    public static string StripVariableEntries(string name, string target, string remove)
    {
        var delimiter = OperatingSystem.IsWindows() ? ';' : ':';

        var targetItems = target.Split(delimiter).ToList();
        var toRemove = remove.Split(delimiter);

        foreach (var item in toRemove)
        {
            var removed = targetItems.Remove(item);
            if (!removed)
            {
                // qWarning() << "Entry" << item << "could not be stripped from variable" << name;
            }
        }

        return string.Join(delimiter, targetItems);
    }

    /// <summary>
    /// Port of CleanEnviroment: the system environment with the dangerous Java variables
    /// (JAVA_ARGS/CLASSPATH/... ) removed, LAUNCHER_* mirrors dropped and the stripped list
    /// (LD_LIBRARY_PATH/LD_PRELOAD on Linux/BSD, QT_PLUGIN_PATH/QT_FONTPATH everywhere)
    /// filtered by the corresponding LAUNCHER_* variable. Consumed by the game process
    /// environment (MinecraftInstance.createEnvironment).
    /// </summary>
    public static ProcessEnvironment CleanEnvironment()
    {
        var rawenv = Environment.GetEnvironmentVariables();
        var env = new ProcessEnvironment();

        var ignored = new List<string>
        {
            "JAVA_ARGS", "CLASSPATH", "CONFIGPATH", "JAVA_HOME",
            "JRE_HOME", "_JAVA_OPTIONS", "JAVA_OPTIONS", "JAVA_TOOL_OPTIONS",
        };

        var stripped = new List<string> { "QT_PLUGIN_PATH", "QT_FONTPATH" };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || IsOpenBsd())
        {
            stripped.Add("LD_LIBRARY_PATH");
            stripped.Add("LD_PRELOAD");
        }

        foreach (var keyObject in rawenv.Keys)
        {
            var key = (string)keyObject;
            var value = (string?)rawenv[key] ?? "";
            // filter out dangerous java crap
            if (ignored.Contains(key))
            {
                continue;
            }

            // These are used to strip the original variables: if there is "LD_LIBRARY_PATH" and
            // "LAUNCHER_LD_LIBRARY_PATH", remove all values in "LAUNCHER_LD_LIBRARY_PATH" from
            // "LD_LIBRARY_PATH".
            if (key.StartsWith("LAUNCHER_", StringComparison.Ordinal))
            {
                continue;
            }

            if (stripped.Contains(key))
            {
                value = StripVariableEntries(key, value, (string?)rawenv["LAUNCHER_" + key] ?? "");
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || IsOpenBsd())
            {
                // Strip IBus (Linux IME framework; for some reason it breaks MC)
                if (key == "XMODIFIERS" && value.Contains("@im=ibus"))
                {
                    value = value.Replace("@im=ibus", "");
                }
            }

            env.Insert(key, value);
        }

        if (OperatingSystem.IsLinux())
        {
            // HACK: Workaround for QTBUG-42500
            if (!env.Contains("LD_LIBRARY_PATH"))
            {
                env.Insert("LD_LIBRARY_PATH", "");
            }
        }

        return env;
    }

    // --------------------------------------------------------------- discovery

    /// <summary>Port of JavaUtils::FindJavaPaths; dispatches to the platform scan.
    /// The JdkFind provider suite is NOT part of this scan anymore — it is the primary
    /// detection layer in <see cref="JavaDetection.DetectAsync"/>; this method produces the
    /// fallback and launcher-owned candidates only.
    /// <paramref name="javaRoot"/> is the launcher-managed java dir (FlintPaths.JavaRoot);
    /// the default keeps the C++ cwd-relative "java" behaviour for bare fixtures.</summary>
    public static List<string> FindJavaPaths(string? javaRoot = null)
    {
        List<string> javas;
        if (OperatingSystem.IsWindows())
        {
            javas = FindJavaPathsWindows();
        }
        else if (OperatingSystem.IsMacOS())
        {
            javas = FindJavaPathsMacOs();
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || IsOpenBsd())
        {
            javas = FindJavaPathsLinux();
        }
        else
        {
            // Unknown operating system build - defaulting to "java"
            javas = [GetDefaultJava().Path];
        }

        javas.AddRange(GetMinecraftJavaBundle());
        javas.AddRange(GetPrismJavaBundle(javaRoot));
        javas = AddJavasFromEnv(javas);
        return DistinctStable(javas);
    }

    /// <summary>
    /// Port of the macOS FindJavaPaths branch: a filesystem scan over the JVM bundle
    /// directories. The Apple Java 6 candidates (the JavaVM.framework, the applet plugin,
    /// the Xcode itms bundle, the /System/Library JVMs and the Contents/Commands layout)
    /// died with the Java &lt;8 support floor — only Contents/Home bundles and the JDK 8
    /// inner JRE variant remain.
    /// </summary>
    private static List<string> FindJavaPathsMacOs()
    {
        var javas = new List<string> { GetDefaultJava().Path };

        var libraryJvmDir = "/Library/Java/JavaVirtualMachines/";
        foreach (var java in DirectoryNames(libraryJvmDir))
        {
            javas.Add(Path.Combine(libraryJvmDir, java, "Contents/Home/bin/java"));
            javas.Add(Path.Combine(libraryJvmDir, java, "Contents/Home/jre/bin/java"));
        }

        var home = Environment.GetEnvironmentVariable("HOME") ?? "";

        // javas downloaded by sdkman
        var sdkmanDir = Environment.GetEnvironmentVariable("SDKMAN_DIR") ?? Path.Combine(home, ".sdkman");
        var sdkmanJavaDir = Path.Combine(sdkmanDir, "candidates/java");
        foreach (var java in DirectoryNames(sdkmanJavaDir))
        {
            javas.Add(Path.Combine(sdkmanJavaDir, java, "bin/java"));
        }

        // javas downloaded by asdf
        var asdfDataDir = Environment.GetEnvironmentVariable("ASDF_DATA_DIR") ?? Path.Combine(home, ".asdf");
        var asdfJavaDir = Path.Combine(asdfDataDir, "installs/java");
        foreach (var java in DirectoryNames(asdfJavaDir))
        {
            javas.Add(Path.Combine(asdfJavaDir, java, "bin/java"));
        }

        // java in user library folder (like from intellij downloads)
        var userLibraryJvmDir = Path.Combine(home, "Library/Java/JavaVirtualMachines");
        foreach (var java in DirectoryNames(userLibraryJvmDir))
        {
            javas.Add(Path.Combine(userLibraryJvmDir, java, "Contents/Home/bin/java"));
        }

        return javas;
    }

    /// <summary>
    /// Port of the Windows FindJavaPaths branch. The vendor registry walks and Program
    /// Files directories are JdkFind's WindowsRegistry/WindowsProgramFiles providers; the
    /// hardcoded candidates keep only Java 8 — the jre6/jre7 entries died with the
    /// Java &lt;8 support floor.
    /// </summary>
    private static List<string> FindJavaPathsWindows()
    {
        var javaCandidates = new List<JavaInstall>
        {
            MakeJavaPtr("C:/Program Files/Java/jre8/bin/javaw.exe"),
            MakeJavaPtr("C:/Program Files (x86)/Java/jre8/bin/javaw.exe"),
            MakeJavaPtr(GetDefaultJava().Path),
        };

        var candidates = new List<string>();
        foreach (var javaCandidate in javaCandidates)
        {
            if (!candidates.Contains(javaCandidate.Path))
            {
                candidates.Add(javaCandidate.Path);
            }
        }

        return candidates;
    }

    /// <summary>Port of the Linux/FreeBSD/OpenBSD FindJavaPaths branch.</summary>
    private static List<string> FindJavaPathsLinux()
    {
        var javas = new List<string> { GetDefaultJava().Path };

        var isLinux = OperatingSystem.IsLinux();
        var snap = Environment.GetEnvironmentVariable("SNAP");

        void ScanJavaDir(string dirPath, Func<string, bool>? filter = null)
        {
            foreach (var entry in DirectoryNames(dirPath))
            {
                if (filter != null && !filter(entry))
                {
                    continue;
                }

                var prefix = CanonicalPath(Path.Combine(dirPath, entry));
                javas.Add(Path.Combine(prefix, "jre/bin/java"));
                javas.Add(Path.Combine(prefix, "bin/java"));
            }
        }

        // java installed in a snap is installed in the standard directory, but underneath $SNAP
        void ScanJavaDirs(string dirPath, Func<string, bool>? filter = null)
        {
            ScanJavaDir(dirPath, filter);
            if (snap is not null)
            {
                ScanJavaDir(Path.Combine(snap, dirPath), filter);
            }
        }

        if (isLinux)
        {
            // oracle RPMs
            ScanJavaDirs("/usr/java");
            // general locations used by distro packaging
            ScanJavaDirs("/usr/lib/jvm");
            ScanJavaDirs("/usr/lib64/jvm");
            ScanJavaDirs("/usr/lib32/jvm");

            // Gentoo's locations for openjdk and openjdk-bin respectively
            static bool GentooFilter(string fileName) =>
                fileName.StartsWith("openjdk-", StringComparison.Ordinal) || fileName.StartsWith("openj9-", StringComparison.Ordinal);

            // AOSC OS's locations for openjdk
            static bool AoscFilter(string fileName) =>
                fileName == "java" || fileName.StartsWith("java-", StringComparison.Ordinal);

            ScanJavaDir("/usr/lib64", GentooFilter);
            ScanJavaDir("/usr/lib", GentooFilter);
            ScanJavaDir("/opt", GentooFilter);
            ScanJavaDir("/usr/lib", AoscFilter);
            // javas stored in Prism Launcher's folder
            ScanJavaDirs("java");
            // manually installed JDKs in /opt
            ScanJavaDirs("/opt/jdk");
            ScanJavaDirs("/opt/jdks");
            ScanJavaDirs("/opt/ibm"); // IBM Semeru Certified Edition
            // flatpak
            ScanJavaDirs("/app/jdk");
        }
        else
        {
            // ports install to /usr/local on OpenBSD & FreeBSD
            ScanJavaDirs("/usr/local");
        }

        var home = Environment.GetEnvironmentVariable("HOME") ?? "";

        // javas downloaded by IntelliJ
        ScanJavaDirs(Path.Combine(home, ".jdks"));
        // javas downloaded by sdkman
        var sdkmanDir = Environment.GetEnvironmentVariable("SDKMAN_DIR") ?? Path.Combine(home, ".sdkman");
        ScanJavaDirs(Path.Combine(sdkmanDir, "candidates/java"));
        // javas downloaded by asdf
        var asdfDataDir = Environment.GetEnvironmentVariable("ASDF_DATA_DIR") ?? Path.Combine(home, ".asdf");
        ScanJavaDirs(Path.Combine(asdfDataDir, "installs/java"));
        // javas downloaded by gradle (toolchains)
        var gradleUserHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME") ?? Path.Combine(home, ".gradle");
        ScanJavaDirs(Path.Combine(gradleUserHome, "jdks"));

        return javas;
    }

    /// <summary>Port of getMinecraftJavaBundle: locate the runtime JVMs the official launcher installed.</summary>
    public static List<string> GetMinecraftJavaBundle()
    {
        var processpaths = new List<string>();
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        if (OperatingSystem.IsMacOS())
        {
            processpaths.Add(Path.Combine(home, "Library/Application Support/minecraft/runtime"));
        }
        else if (OperatingSystem.IsWindows())
        {
            var appDataPath = Environment.GetEnvironmentVariable("APPDATA") ?? "";
            processpaths.Add(Path.Combine(Path.GetFullPath(appDataPath), ".minecraft", "runtime"));

            // add the microsoft store version of the launcher to the search. the current path is:
            // C:\Users\USERNAME\AppData\Local\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime
            var localAppDataPath = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var minecraftMsStorePath = new LocalPath(Path.GetFullPath(localAppDataPath)) / "Packages" / "Microsoft.4297127D64EC6_8wekyb3d8bbwe";
            processpaths.Add((minecraftMsStorePath / "LocalCache" / "Local" / "runtime").Value);
        }
        else
        {
            processpaths.Add(Path.Combine(home, ".minecraft", "runtime"));
        }

        var javas = new List<string>();
        while (processpaths.Count > 0)
        {
            var dirPath = processpaths[0];
            processpaths.RemoveAt(0);
            if (!Directory.Exists(dirPath))
            {
                continue;
            }

            var entries = Directory.GetDirectories(dirPath);
            var binFound = false;
            foreach (var entry in entries)
            {
                if (Path.GetFileName(entry) == "bin")
                {
                    javas.Add(Path.Combine(CanonicalPath(entry), JavaExecutable));
                    binFound = true;
                    break;
                }
            }

            if (!binFound)
            {
                processpaths.AddRange(entries.Select(CanonicalPath));
            }
        }

        return javas;
    }

    /// <summary>Port of getPrismJavaBundle: locate the JVMs inside the launcher-managed java
    /// dir (<paramref name="javaRoot"/> = FlintPaths.JavaRoot; default keeps the C++
    /// cwd-relative "java" behaviour for bare fixtures).</summary>
    public static List<string> GetPrismJavaBundle(string? javaRoot = null)
    {
        var javas = new List<string>();

        void ScanDir(string prefix)
        {
            javas.Add(Path.Combine(prefix, "jre", "bin", JavaExecutable));
            javas.Add(Path.Combine(prefix, "bin", JavaExecutable));
            javas.Add(Path.Combine(prefix, JavaExecutable));
        }

        void ScanJavaDir(string dirPath)
        {
            if (!Directory.Exists(dirPath))
            {
                return;
            }

            foreach (var entry in Directory.GetDirectories(dirPath))
            {
                ScanDir(CanonicalPath(entry));
            }
        }

        ScanJavaDir(javaRoot ?? Path.GetFullPath("java"));

        return javas;
    }

    /// <summary>
    /// Port of addJavasFromEnv: extra candidate paths from the FLINT_JAVA_PATHS environment
    /// variable (upstream: BuildConfig.LAUNCHER_ENVNAME + "_JAVA_PATHS"); on Windows the PATH
    /// entries are probed for javaw.exe as well.
    /// </summary>
    private static List<string> AddJavasFromEnv(List<string> javas)
    {
        var env = Environment.GetEnvironmentVariable($"{FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS") ?? "";
        List<string> javaPaths;
        if (OperatingSystem.IsWindows())
        {
            javaPaths = env.Replace("\\", "/").Split(';').ToList();

            var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            javaPaths.AddRange(envPath.Replace("\\", "/").Split(';').Select(s => s + "/javaw.exe"));
        }
        else
        {
            javaPaths = env.Split(':').ToList();
        }

        javas.AddRange(javaPaths);
        return javas;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>OperatingSystem has no IsOpenBSD() helper (see RuntimeContext for the same shim).</summary>
#pragma warning disable CA1418 // "OpenBSD" is not a platform name the analyzer knows
    private static bool IsOpenBsd() => OperatingSystem.IsOSPlatform("OpenBSD");
#pragma warning restore CA1418

    /// <summary>QDir::entryList(Dirs | NoDotAndDotDot) equivalent (names only, empty when missing).</summary>
    private static List<string> DirectoryNames(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            return [];
        }

        var names = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateDirectories(dirPath))
            {
                names.Add(Path.GetFileName(entry));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // unreadable directories simply contribute no candidates
        }

        return names;
    }

    /// <summary>QFileInfo::canonicalFilePath approximation (resolves the symlink chain, best effort).</summary>
    private static string CanonicalPath(string path)
    {
        try
        {
            var final = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return Path.GetFullPath(final?.FullName ?? path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>QList::removeDuplicates(): drop later duplicates, keep the first occurrence order.</summary>
    private static List<string> DistinctStable(List<string> javas)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(javas.Count);
        foreach (var java in javas)
        {
            if (seen.Add(java))
            {
                result.Add(java);
            }
        }

        return result;
    }
}
