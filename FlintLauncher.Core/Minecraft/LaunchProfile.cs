// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/LaunchProfile.{h,cpp}: the merged profile a set of version
// patches is applied to. The Qt model/ProblemProvider plumbing is kept; only the UI-facing
// getProblems() FIXME (always empty upstream as well) stays as-is.

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Minecraft;

public class LaunchProfile : ProblemProvider
{
    private string minecraftVersion = "";

    /// Release type - "release" or "snapshot"
    private string minecraftVersionType = "";

    /// Assets type - "legacy" or a version ID
    private MojangAssetIndexInfo? minecraftAssets;

    /**
     * arguments that should be used for launching minecraft
     *
     * ex: "--username ${auth_player_name} --session ${auth_session}
     *      --version ${version_name} --gameDir ${game_directory} --assetsDir ${game_assets}"
     */
    private string minecraftArguments = "";

    /**
     * Additional arguments to pass to the JVM in addition to those the user has configured,
     * memory settings, etc.
     */
    private readonly List<string> _addnJvmArguments = [];

    /// A list of all tweaker classes
    private List<string> tweakers = [];

    /// The main class to load first
    private string mainClass = "";

    /// The applet class, for some very old minecraft releases
    private string appletClass = "";

    /// the list of libraries
    private readonly List<Library> _libraries = [];

    /// the list of maven files to be placed in the libraries folder, but not acted upon
    private readonly List<Library> _mavenFiles = [];

    /// the list of java agents to add to JVM arguments
    private readonly List<Agent> _agents = [];

    /// the main jar
    private Library? mainJar;

    /// the list of native libraries
    private readonly List<Library> _nativeLibraries = [];

    /// traits, collected from all the version files (version files can only add)
    private readonly HashSet<string> _traits = new(StringComparer.Ordinal);

    /// A list of jar mods. version files can add those.
    private readonly List<Library> _jarMods = [];

    /// the list of mods
    private readonly List<Library> _mods = [];

    /// compatible java major versions
    private readonly List<int> _compatibleJavaMajors = [];

    private string compatibleJavaName = "";

    private ProblemSeverity problemSeverity = ProblemSeverity.None;

    /// <summary>clear the profile</summary>
    public void Clear()
    {
        minecraftVersion = "";
        minecraftVersionType = "";
        minecraftAssets = null;
        minecraftArguments = "";
        _addnJvmArguments.Clear();
        tweakers.Clear();
        mainClass = "";
        appletClass = "";
        _libraries.Clear();
        _mavenFiles.Clear();
        _agents.Clear();
        _traits.Clear();
        _jarMods.Clear();
        // NOTE upstream: clear() does not reset m_mods, m_nativeLibraries, m_compatibleJavaMajors
        // or m_compatibleJavaName — kept as-is
        mainJar = null;
        problemSeverity = ProblemSeverity.None;
    }

    private static void ApplyString(string from, ref string to)
    {
        if (from.Length == 0)
        {
            return;
        }

        to = from;
    }

    // ------------------------------------------------------------- application of patches

    public void ApplyMinecraftVersion(string id) => ApplyString(id, ref minecraftVersion);

    public void ApplyAppletClass(string appletClass) => ApplyString(appletClass, ref this.appletClass);

    public void ApplyMainClass(string mainClass) => ApplyString(mainClass, ref this.mainClass);

    public void ApplyMinecraftArguments(string minecraftArguments) => ApplyString(minecraftArguments, ref this.minecraftArguments);

    public void ApplyAddnJvmArguments(IReadOnlyList<string> addnJvmArguments) => _addnJvmArguments.AddRange(addnJvmArguments);

    public void ApplyMinecraftVersionType(string type) => ApplyString(type, ref minecraftVersionType);

    public void ApplyMinecraftAssets(MojangAssetIndexInfo? assets)
    {
        if (assets is not null)
        {
            minecraftAssets = assets;
        }
    }

    public void ApplyTraits(IEnumerable<string> traits)
    {
        _traits.UnionWith(traits);
    }

    public void ApplyTweakers(IReadOnlyList<string> tweakers)
    {
        // if the applied tweakers override an existing one, skip it. this effectively moves it later in the sequence
        var newTweakers = new List<string>();
        foreach (var tweaker in this.tweakers)
        {
            if (tweakers.Contains(tweaker))
            {
                continue;
            }

            newTweakers.Add(tweaker);
        }

        // then just append the new tweakers (or moved original ones)
        newTweakers.AddRange(tweakers);
        this.tweakers = newTweakers;
    }

    public void ApplyJarMods(IReadOnlyList<Library> jarMods) => _jarMods.AddRange(jarMods);

    private static int FindLibraryByName(List<Library> haystack, GradleSpecifier needle)
    {
        var retval = -1;
        for (var i = 0; i < haystack.Count; ++i)
        {
            if (haystack[i].RawName().MatchName(needle))
            {
                // only one is allowed.
                if (retval != -1)
                {
                    return -1;
                }

                retval = i;
            }
        }

        return retval;
    }

    public void ApplyMods(IReadOnlyList<Library> mods)
    {
        foreach (var mod in mods)
        {
            var modCopy = Library.LimitedCopy(mod);

            // find the mod by name.
            var index = FindLibraryByName(_mods, mod.RawName());
            // mod not found? just add it.
            if (index < 0)
            {
                _mods.Add(modCopy);
                return;
            }

            var existingLibrary = _mods[index];
            // if we are higher it means we should update
            if (new McVersion(mod.Version()) > new McVersion(existingLibrary.Version()))
            {
                _mods[index] = modCopy;
            }
        }
    }

    public void ApplyCompatibleJavaMajors(IReadOnlyList<int> javaMajor) => _compatibleJavaMajors.AddRange(javaMajor);

    public void ApplyCompatibleJavaName(string javaName)
    {
        if (javaName.Length != 0)
        {
            compatibleJavaName = javaName;
        }
    }

    public void ApplyLibrary(Library library, RuntimeContext runtimeContext)
    {
        if (!library.IsActive(runtimeContext))
        {
            return;
        }

        List<Library> list = library.IsNative() ? _nativeLibraries : _libraries;

        var libraryCopy = Library.LimitedCopy(library);

        // find the library by name.
        var index = FindLibraryByName(list, library.RawName());
        // library not found? just add it.
        if (index < 0)
        {
            list.Add(libraryCopy);
            return;
        }

        var existingLibrary = list[index];
        // if we are higher it means we should update
        if (new McVersion(library.Version()) > new McVersion(existingLibrary.Version()))
        {
            list[index] = libraryCopy;
        }
    }

    public void ApplyMavenFile(Library mavenFile, RuntimeContext runtimeContext)
    {
        if (!mavenFile.IsActive(runtimeContext))
        {
            return;
        }

        if (mavenFile.IsNative())
        {
            return;
        }

        // unlike libraries, we do not keep only one version or try to dedupe them
        _mavenFiles.Add(Library.LimitedCopy(mavenFile));
    }

    public void ApplyAgent(Agent agent, RuntimeContext runtimeContext)
    {
        var lib = agent.Library;
        if (!lib.IsActive(runtimeContext))
        {
            return;
        }

        if (lib.IsNative())
        {
            return;
        }

        _agents.Add(agent);
    }

    public void ApplyMainJar(Library? jar)
    {
        if (jar is not null)
        {
            mainJar = jar;
        }
    }

    public void ApplyProblemSeverity(ProblemSeverity severity)
    {
        if (problemSeverity < severity)
        {
            problemSeverity = severity;
        }
    }

    // ---------------------------------------------------------------- getters

    public string GetMinecraftVersion() => minecraftVersion;

    public string GetMainClass() => mainClass;

    public string GetAppletClass() => appletClass;

    public string GetMinecraftVersionType() => minecraftVersionType;

    public MojangAssetIndexInfo GetMinecraftAssets()
    {
        if (minecraftAssets is null)
        {
            return new MojangAssetIndexInfo("legacy");
        }

        return minecraftAssets;
    }

    public string GetMinecraftArguments() => minecraftArguments;

    public IReadOnlyList<string> GetAddnJvmArguments() => _addnJvmArguments;

    public IReadOnlySet<string> GetTraits() => _traits;

    public IReadOnlyList<string> GetTweakers() => tweakers;

    public IReadOnlyList<Library> GetJarMods() => _jarMods;

    public IReadOnlyList<Library> GetLibraries() => _libraries;

    public IReadOnlyList<Library> GetNativeLibraries() => _nativeLibraries;

    public IReadOnlyList<Library> GetMavenFiles() => _mavenFiles;

    public IReadOnlyList<Agent> GetAgents() => _agents;

    public IReadOnlyList<int> GetCompatibleJavaMajors() => _compatibleJavaMajors;

    public string GetCompatibleJavaName() => compatibleJavaName;

    public Library? GetMainJar() => mainJar;

    public bool HasTrait(string trait) => _traits.Contains(trait);

    public void GetLibraryFiles(
        RuntimeContext runtimeContext,
        List<string> jars,
        List<string> nativeJars,
        string overridePath,
        string tempPath,
        bool addJarMods = true,
        string storageBase = "")
    {
        var native32 = new List<string>();
        var native64 = new List<string>();
        jars.Clear();
        nativeJars.Clear();
        foreach (var lib in GetLibraries())
        {
            lib.GetApplicableFiles(runtimeContext, jars, nativeJars, native32, native64, overridePath, storageBase);
        }

        // NOTE: order is important here, add main jar last to the lists
        if (mainJar is not null)
        {
            // FIXME: HACK!! jar modding is weird and unsystematic!
            if (_jarMods.Count != 0 && addJarMods)
            {
                jars.Add(Path.GetFullPath(FileSystem.PathCombine(tempPath, "minecraft.jar")));
            }
            else
            {
                mainJar.GetApplicableFiles(runtimeContext, jars, nativeJars, native32, native64, overridePath, storageBase);
            }
        }

        foreach (var lib in GetNativeLibraries())
        {
            lib.GetApplicableFiles(runtimeContext, jars, nativeJars, native32, native64, overridePath, storageBase);
        }

        if (runtimeContext.JavaArchitecture == "32")
        {
            nativeJars.AddRange(native32);
        }
        else if (runtimeContext.JavaArchitecture == "64")
        {
            nativeJars.AddRange(native64);
        }
    }

    public override ProblemSeverity GetProblemSeverity() => problemSeverity;

    public override IReadOnlyList<PatchProblem> GetProblems()
    {
        // FIXME upstream: implement something that actually makes sense here
        return [];
    }
}
