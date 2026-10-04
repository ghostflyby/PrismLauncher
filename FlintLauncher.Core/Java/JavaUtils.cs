// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaUtils.{h,cpp} (JVM discovery + the process-environment cleaner).
//
// The C++ class is stateless, so the port flattens it into a static class. Discovery is now
// layered, with the JavaChecker run remaining the validity gate (enumeration may over-produce):
//  - Layer 1 — JdkFind provider suite (NuGet JdkFind, Apache-2.0): validated Java homes
//    (bin/java + release, plain / macOS-bundle / Homebrew-keg layouts) from JAVA_HOME, PATH,
//    the macOS JVM directories, /usr/lib/jvm, Homebrew kegs, Windows registry + Program Files
//    vendor dirs, ~/.jdks, SDKMAN, Gradle toolchains, Jabba and Scoop. This closes the
//    documented Windows-registry stub below and adds the sources the C++ scan never had.
//  - Layer 2 — the C++-parity filesystem scan (the per-platform branches): fixed legacy
//    candidates, release-less JVMs (old Apple/Oracle runtimes), the distro directories and
//    name filters JdkFind deliberately does not model (Gentoo/AOSC, /usr/java, /usr/lib64/jvm,
//    /opt/jdk*, /opt/ibm, /app/jdk, $SNAP mirroring, asdf, FreeBSD/OpenBSD /usr/local).
//  - Layer 3 — launcher-owned sources: the Minecraft launcher runtime bundle, the
//    launcher-managed java root, the FLINT_JAVA_PATHS environment override.
using System.Runtime.InteropServices;
using JdkFind;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Java;

public static class JavaUtils
{
    /// <summary>Port of JavaUtils::javaExecutable ("javaw.exe" on Windows, "java" elsewhere).</summary>
    public static string JavaExecutable => OperatingSystem.IsWindows() ? "javaw.exe" : "java";

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
    /// filtered by the corresponding LAUNCHER_* variable. Consumed by JavaChecker when
    /// spawning the check process.
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
            javas = FindJavaPathsMacOS();
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

        // Layer 1 on top of the legacy platform scan: the JdkFind provider suite. Overlap
        // between the layers is harmless — the exact-string dedup below collapses identical
        // candidates, and the checker run is the validity gate either way.
        javas.AddRange(FindJavaPathsFromProviders(JdkFinder.Default.Providers));

        javas.AddRange(GetMinecraftJavaBundle());
        javas.AddRange(GetPrismJavaBundle(javaRoot));
        javas = AddJavasFromEnv(javas);
        return DistinctStable(javas);
    }

    /// <summary>Layer-1 seam over the JdkFind provider suite: every provider's validated Java
    /// homes (each already passed <c>JavaHomeLayout.Probe</c> — bin/java + release, plain,
    /// macOS-bundle or Homebrew-keg layout) mapped to the launcher executable path. Providers
    /// self-noop on the platforms they do not cover, so the suite is passed through untouched;
    /// duplicate homes across providers (JAVA_HOME pointing into a scanned directory, ...) are
    /// collapsed with the same exact-string dedup the composition uses. The suite deliberately
    /// bypasses <c>JdkFinder.Locate</c>: its release-file parsing would drop release-less JVMs
    /// the checker can still validate, and its runtime probe spawns a process per JVM that the
    /// checker already runs.</summary>
    internal static List<string> FindJavaPathsFromProviders(IReadOnlyList<IJvmProvider> providers)
    {
        var javas = new List<string>();
        foreach (var provider in providers)
        {
            foreach (var home in provider.GetJavaHomes())
            {
                javas.Add(FileSystem.PathCombine(home, "bin", JavaExecutable));
            }
        }
        return DistinctStable(javas);
    }

    /// <summary>
    /// Port of the macOS FindJavaPaths branch: a filesystem scan over the JVM bundle
    /// directories (upstream does not invoke /usr/libexec/java_home).
    /// </summary>
    private static List<string> FindJavaPathsMacOS()
    {
        var javas = new List<string>
        {
            GetDefaultJava().Path,
            "/Applications/Xcode.app/Contents/Applications/Application Loader.app/Contents/MacOS/itms/java/bin/java",
            "/Library/Internet Plug-Ins/JavaAppletPlugin.plugin/Contents/Home/bin/java",
            "/System/Library/Frameworks/JavaVM.framework/Versions/Current/Commands/java",
        };

        var libraryJVMDir = "/Library/Java/JavaVirtualMachines/";
        foreach (var java in DirectoryNames(libraryJVMDir))
        {
            javas.Add(FileSystem.PathCombine(libraryJVMDir, java, "Contents/Home/bin/java"));
            javas.Add(FileSystem.PathCombine(libraryJVMDir, java, "Contents/Home/jre/bin/java"));
        }
        var systemLibraryJVMDir = "/System/Library/Java/JavaVirtualMachines/";
        foreach (var java in DirectoryNames(systemLibraryJVMDir))
        {
            javas.Add(FileSystem.PathCombine(systemLibraryJVMDir, java, "Contents/Home/bin/java"));
            javas.Add(FileSystem.PathCombine(systemLibraryJVMDir, java, "Contents/Commands/java"));
        }

        var home = Environment.GetEnvironmentVariable("HOME") ?? "";

        // javas downloaded by sdkman
        var sdkmanDir = Environment.GetEnvironmentVariable("SDKMAN_DIR") ?? FileSystem.PathCombine(home, ".sdkman");
        var sdkmanJavaDir = FileSystem.PathCombine(sdkmanDir, "candidates/java");
        foreach (var java in DirectoryNames(sdkmanJavaDir))
        {
            javas.Add(FileSystem.PathCombine(sdkmanJavaDir, java, "bin/java"));
        }

        // javas downloaded by asdf
        var asdfDataDir = Environment.GetEnvironmentVariable("ASDF_DATA_DIR") ?? FileSystem.PathCombine(home, ".asdf");
        var asdfJavaDir = FileSystem.PathCombine(asdfDataDir, "installs/java");
        foreach (var java in DirectoryNames(asdfJavaDir))
        {
            javas.Add(FileSystem.PathCombine(asdfJavaDir, java, "bin/java"));
        }

        // java in user library folder (like from intellij downloads)
        var userLibraryJVMDir = FileSystem.PathCombine(home, "Library/Java/JavaVirtualMachines/");
        foreach (var java in DirectoryNames(userLibraryJVMDir))
        {
            javas.Add(FileSystem.PathCombine(userLibraryJVMDir, java, "Contents/Home/bin/java"));
            javas.Add(FileSystem.PathCombine(userLibraryJVMDir, java, "Contents/Commands/java"));
        }

        return javas;
    }

    /// <summary>
    /// Port of the Windows FindJavaPaths branch. The registry walks are stubbed out (see
    /// <see cref="FindJavaFromRegistryKey"/>); the well-known-path candidates, the bundles and
    /// the env fallback cover the common locations.
    /// </summary>
    private static List<string> FindJavaPathsWindows()
    {
        var javaCandidates = new List<JavaInstall>();

        // Oracle / AdoptOpenJDK / Eclipse Adoptium / IBM Semeru / Microsoft / Azul Zulu /
        // BellSoft Liberica registry walks — all stubbed (see FindJavaFromRegistryKey).
        javaCandidates.Add(MakeJavaPtr("C:/Program Files/Java/jre8/bin/javaw.exe"));
        javaCandidates.Add(MakeJavaPtr("C:/Program Files/Java/jre7/bin/javaw.exe"));
        javaCandidates.Add(MakeJavaPtr("C:/Program Files/Java/jre6/bin/javaw.exe"));

        javaCandidates.Add(MakeJavaPtr("C:/Program Files (x86)/Java/jre8/bin/javaw.exe"));
        javaCandidates.Add(MakeJavaPtr("C:/Program Files (x86)/Java/jre7/bin/javaw.exe"));
        javaCandidates.Add(MakeJavaPtr("C:/Program Files (x86)/Java/jre6/bin/javaw.exe"));

        javaCandidates.Add(MakeJavaPtr(GetDefaultJava().Path));

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

    /// <summary>
    /// Port of JavaUtils::FindJavaFromRegistryKey (Windows only). The port's original stub
    /// (Win32 P/Invoke registry walks were out of scope) is superseded by the JdkFind layer:
    /// <c>WindowsRegistryJvmProvider</c> walks the JavaSoft / Eclipse Adoptium / Microsoft /
    /// Azul / Amazon Corretto roots via Microsoft.Win32 (HKLM 64-bit view) and
    /// <c>WindowsProgramFilesJvmProvider</c> covers the vendor directories under
    /// %ProgramFiles%. This stub is kept only as the marker of the remaining delta vs the
    /// C++ walk (AdoptOpenJDK/IBM Semeru roots and the WOW64 32-bit view, both of which
    /// JdkFind scopes out deliberately) and returns nothing.
    /// </summary>
    private static List<JavaInstall> FindJavaFromRegistryKey(uint keyType, string keyName, string keyJavaDir, string subkeySuffix = "")
    {
        return [];
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
                var prefix = CanonicalPath(FileSystem.PathCombine(dirPath, entry));
                javas.Add(FileSystem.PathCombine(prefix, "jre/bin/java"));
                javas.Add(FileSystem.PathCombine(prefix, "bin/java"));
            }
        }
        // java installed in a snap is installed in the standard directory, but underneath $SNAP
        void ScanJavaDirs(string dirPath, Func<string, bool>? filter = null)
        {
            ScanJavaDir(dirPath, filter);
            if (snap is not null)
            {
                ScanJavaDir(FileSystem.PathCombine(snap, dirPath), filter);
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
        ScanJavaDirs(FileSystem.PathCombine(home, ".jdks"));
        // javas downloaded by sdkman
        var sdkmanDir = Environment.GetEnvironmentVariable("SDKMAN_DIR") ?? FileSystem.PathCombine(home, ".sdkman");
        ScanJavaDirs(FileSystem.PathCombine(sdkmanDir, "candidates/java"));
        // javas downloaded by asdf
        var asdfDataDir = Environment.GetEnvironmentVariable("ASDF_DATA_DIR") ?? FileSystem.PathCombine(home, ".asdf");
        ScanJavaDirs(FileSystem.PathCombine(asdfDataDir, "installs/java"));
        // javas downloaded by gradle (toolchains)
        var gradleUserHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME") ?? FileSystem.PathCombine(home, ".gradle");
        ScanJavaDirs(FileSystem.PathCombine(gradleUserHome, "jdks"));

        return javas;
    }

    /// <summary>Port of JavaUtils::getJavaCheckPath (Application::getJarPath("JavaCheck.jar")).
    /// <paramref name="installRoot"/> is the launcher installation root (FlintPaths.InstallRoot);
    /// the default keeps the .NET app base directory for bare fixtures.</summary>
    public static string GetJavaCheckPath(string? installRoot = null)
    {
        var root = installRoot ?? AppContext.BaseDirectory;
        var potentialPaths = new List<string>();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || IsOpenBsd())
        {
            potentialPaths.Add(FileSystem.PathCombine(root, "share", FlintConfig.Default.LauncherName));
        }
        potentialPaths.Add(FileSystem.PathCombine(root, "jars"));
        potentialPaths.Add(FileSystem.PathCombine(AppContext.BaseDirectory, "jars"));
        potentialPaths.Add(FileSystem.PathCombine(AppContext.BaseDirectory, "..", "jars")); // from inside build dir, for debugging

        foreach (var p in potentialPaths)
        {
            var jarPath = FileSystem.PathCombine(p, JavaCheckJar.JarFileName);
            if (File.Exists(jarPath))
            {
                return jarPath;
            }
        }

        // nothing packaged: fall back to the cache copy of the embedded checker (a documented
        // P5-a stub that currently always fails, see JavaCheckJar.TryExtractTo)
        if (File.Exists(JavaCheckJar.DefaultExtractionPath))
        {
            return JavaCheckJar.DefaultExtractionPath;
        }
        if (JavaCheckJar.TryExtractTo(JavaCheckJar.DefaultExtractionPath, out _, installRoot))
        {
            return JavaCheckJar.DefaultExtractionPath;
        }
        return "";
    }

    /// <summary>Port of getMinecraftJavaBundle: locate the runtime JVMs the official launcher installed.</summary>
    public static List<string> GetMinecraftJavaBundle()
    {
        var processpaths = new List<string>();
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        if (OperatingSystem.IsMacOS())
        {
            processpaths.Add(FileSystem.PathCombine(home, "Library/Application Support/minecraft/runtime"));
        }
        else if (OperatingSystem.IsWindows())
        {
            var appDataPath = Environment.GetEnvironmentVariable("APPDATA") ?? "";
            processpaths.Add(FileSystem.PathCombine(Path.GetFullPath(appDataPath), ".minecraft", "runtime"));

            // add the microsoft store version of the launcher to the search. the current path is:
            // C:\Users\USERNAME\AppData\Local\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime
            var localAppDataPath = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var minecraftMSStorePath = FileSystem.PathCombine(Path.GetFullPath(localAppDataPath), "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe");
            processpaths.Add(FileSystem.PathCombine(minecraftMSStorePath, "LocalCache", "Local", "runtime"));
        }
        else
        {
            processpaths.Add(FileSystem.PathCombine(home, ".minecraft", "runtime"));
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
                    javas.Add(FileSystem.PathCombine(CanonicalPath(entry), JavaExecutable));
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
            javas.Add(FileSystem.PathCombine(prefix, "jre", "bin", JavaExecutable));
            javas.Add(FileSystem.PathCombine(prefix, "bin", JavaExecutable));
            javas.Add(FileSystem.PathCombine(prefix, JavaExecutable));
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
