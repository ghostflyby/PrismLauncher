// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/VersionFile.{h,cpp}: the OneSix/Mojang version JSON model plus
// its application to a LaunchProfile. As in Library.cs, the C++ friend-accessed data members
// become public properties.

using FlintLauncher.Core.Java;
using FlintLauncher.Core.Meta;

namespace FlintLauncher.Core.Minecraft;

public class VersionFile : ProblemContainer
{
    /// Prism Launcher: order hint for this version file if no explicit order is set
    public int Order { get; set; }

    /// Prism Launcher: human readable name of this package
    public string Name { get; set; } = "";

    /// Prism Launcher: package ID of this package
    public string Uid { get; set; } = "";

    /// Prism Launcher: version of this package
    public string Version { get; set; } = "";

    /// Prism Launcher: DEPRECATED dependency on a Minecraft version
    public string DependsOnMinecraftVersion { get; set; } = "";

    /// Mojang: DEPRECATED used to version the Mojang version format
    public int MinimumLauncherVersion { get; set; } = -1;

    /// Mojang: DEPRECATED version of Minecraft this is
    public string MinecraftVersion { get; set; } = "";

    /// Mojang: class to launch Minecraft with
    public string MainClass { get; set; } = "";

    /// Prism Launcher: class to launch legacy Minecraft with (embed in a custom window)
    public string AppletClass { get; set; } = "";

    /// Mojang: Minecraft launch arguments (may contain placeholders for variable substitution)
    public string MinecraftArguments { get; set; } = "";

    /// Prism Launcher: Additional JVM launch arguments
    public List<string> AddnJvmArguments { get; } = [];

    /// Mojang: list of compatible java majors
    public List<int> CompatibleJavaMajors { get; } = [];

    /// Mojang: the name of recommended java version
    public string CompatibleJavaName { get; set; } = "";

    /// Mojang: type of the Minecraft version
    public string Type { get; set; } = "";

    /// Mojang: the time this version was actually released by Mojang (null = invalid QDateTime)
    public DateTimeOffset? ReleaseTime { get; set; }

    /// Mojang: DEPRECATED the time this version was last updated by Mojang (null = invalid QDateTime)
    public DateTimeOffset? UpdateTime { get; set; }

    /// Mojang: DEPRECATED asset group to be used with Minecraft
    public string Assets { get; set; } = "";

    /// Prism Launcher: list of tweaker mod arguments for launchwrapper
    public List<string> AddTweakers { get; } = [];

    /// Mojang: list of libraries to add to the version
    public List<Library> Libraries { get; } = [];

    /// Prism Launcher: list of maven files to put in the libraries folder, but not in classpath
    public List<Library> MavenFiles { get; } = [];

    /// Prism Launcher: list of agents to add to JVM arguments
    public List<Agent> Agents { get; } = [];

    /// The main jar (Minecraft version library, normally)
    public Library? MainJar { get; set; }

    /// Prism Launcher: list of attached traits of this version file - used to enable features
    public HashSet<string> Traits { get; } = new(StringComparer.Ordinal);

    /// Prism Launcher: list of jar mods added to this version
    public List<Library> JarMods { get; } = [];

    /// Prism Launcher: list of mods added to this version
    public List<Library> Mods { get; } = [];

    /**
     * Prism Launcher: set of packages this depends on
     * NOTE: this is shared with the meta format!!!
     */
    public SortedSet<Require> Requires { get; set; } = new();

    /**
     * Prism Launcher: set of packages this conflicts with
     * NOTE: this is shared with the meta format!!!
     */
    public SortedSet<Require> Conflicts { get; set; } = new();

    /// is volatile -- may be removed as soon as it is no longer needed by something else
    public bool Volatile { get; set; }

    public List<JavaMetadata> Runtimes { get; set; } = [];

    // Mojang: DEPRECATED list of 'downloads' - client jar, server jar, windows server exe, maybe more.
    public Dictionary<string, MojangDownloadInfo> MojangDownloads { get; } = [];

    // Mojang: extended asset index download information
    public MojangAssetIndexInfo? MojangAssetIndex { get; set; }

    private static bool IsMinecraftVersion(string uid)
    {
        return uid == "net.minecraft";
    }

    public void ApplyTo(LaunchProfile profile, RuntimeContext runtimeContext)
    {
        // Only real Minecraft can set those. Don't let anything override them.
        if (IsMinecraftVersion(Uid))
        {
            profile.ApplyMinecraftVersion(Version);
            profile.ApplyMinecraftVersionType(Type);
            // HACK: ignore assets from other version files than Minecraft
            // workaround for stupid assets issue caused by amazon:
            // https://www.theregister.co.uk/2017/02/28/aws_is_awol_as_s3_goes_haywire/
            profile.ApplyMinecraftAssets(MojangAssetIndex);
        }

        profile.ApplyMainJar(MainJar);
        profile.ApplyMainClass(MainClass);
        profile.ApplyAppletClass(AppletClass);
        profile.ApplyMinecraftArguments(MinecraftArguments);
        profile.ApplyAddnJvmArguments(AddnJvmArguments);
        profile.ApplyTweakers(AddTweakers);
        profile.ApplyJarMods(JarMods);
        profile.ApplyMods(Mods);
        profile.ApplyTraits(Traits);
        profile.ApplyCompatibleJavaMajors(CompatibleJavaMajors);
        profile.ApplyCompatibleJavaName(CompatibleJavaName);

        foreach (var library in Libraries)
        {
            profile.ApplyLibrary(library, runtimeContext);
        }

        foreach (var mavenFile in MavenFiles)
        {
            profile.ApplyMavenFile(mavenFile, runtimeContext);
        }

        foreach (var agent in Agents)
        {
            profile.ApplyAgent(agent, runtimeContext);
        }

        profile.ApplyProblemSeverity(GetProblemSeverity());
    }
}
