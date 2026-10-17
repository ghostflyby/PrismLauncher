// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/technic/TechnicPackProcessor.{h,cpp}: post-process an extracted
// Technic pack into a runnable instance. It detects the pack "flavor":
//   minecraft/bin/modpack.jar  -> jar-modded pack (optionally carrying version.json /
//                                 fmlversion.properties / forgeversion.properties)
//   minecraft/bin/version.json -> plain modded pack
//   neither                    -> the "vanilla" flavor (excluded by the search code upstream)
// and writes the instance.cfg keys (name/InstanceType/iconKey) plus the component versions
// (net.minecraft always; net.minecraftforge/net.neoforged/net.fabricmc.fabric-loader/
// org.quiltmc.quilt-loader detected from version.json's libraries or the jar's properties).
// The C++ QObject signals become a nullable-string return: null = succeeded, otherwise the
// failure reason.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.ModPlatform.Technic;

public static class TechnicPackProcessor
{
    /// <summary>Port of Technic::TechnicPackProcessor::run (see the header note). Async:
    /// the important:true SetComponentVersion calls resolve against the meta index (network),
    /// so the flow awaits instead of blocking a thread (the former .GetAwaiter().GetResult()
    /// sync-over-async is retired).</summary>
    public static async Task<string?> RunAsync(InstanceEnvironment environment,
        string instName,
        string instIcon,
        string stagingPath,
        string minecraftVersion = "",
        bool isSolder = false,
        CancellationToken cancellationToken = default)
    {
        _ = isSolder; // (unused upstream too)
        var minecraftPath = FileSystem.PathCombine(stagingPath, "minecraft");
        // D4: the staged instance is Flint format from birth (typed instance.json adapter)
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(stagingPath, environment), stagingPath);

        instance.SetName(instName);

        if (instIcon != "default")
        {
            instance.SetIconKey(instIcon);
        }

        var components = instance.GetPackProfile();
        components.BuildingFromScratch();

        string data;

        var modpackJar = FileSystem.PathCombine(minecraftPath, "bin", "modpack.jar");
        var versionJson = FileSystem.PathCombine(minecraftPath, "bin", "version.json");
        var fmlMinecraftVersion = "";
        if (File.Exists(modpackJar))
        {
            ZipArchive zipFile;
            try
            {
                zipFile = ZipFile.OpenRead(modpackJar);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                return "Unable to open \"bin/modpack.jar\" file!";
            }

            using (zipFile)
            {
                if (ZipEntryExists(zipFile, "version.json"))
                {
                    if (ZipEntryExists(zipFile, "fmlversion.properties"))
                    {
                        var fmlVersionData = ReadZipEntryText(zipFile, "fmlversion.properties");
                        if (fmlVersionData is null)
                        {
                            return "Unable to open \"fmlversion.properties\"!";
                        }

                        var iniFile = new IniFile();
                        iniFile.LoadText(fmlVersionData);
                        // If not present, this evaluates to a null string
                        fmlMinecraftVersion = iniFile.GetString("fmlbuild.mcversion", "");
                    }

                    var versionData = ReadZipEntryText(zipFile, "version.json");
                    if (versionData is null)
                    {
                        return "Unable to open \"version.json\"!";
                    }

                    data = versionData;
                }
                else
                {
                    if (minecraftVersion.Length == 0)
                    {
                        return "Could not find \"version.json\" inside \"bin/modpack.jar\", but Minecraft version is unknown";
                    }

                    _ = await components.SetComponentVersion("net.minecraft", minecraftVersion, important: true,
                        cancellationToken: cancellationToken);
                    _ = components.InstallJarMods([modpackJar]);

                    // Forge for 1.4.7 and for 1.5.2 require extra libraries.
                    // Figure out the forge version and add it as a component
                    // (the code still comes from the jar mod installed above)
                    if (ZipEntryExists(zipFile, "forgeversion.properties"))
                    {
                        var forgeVersionData = ReadZipEntryText(zipFile, "forgeversion.properties");
                        if (forgeVersionData is null)
                        {
                            // Really shouldn't happen, but error handling shall not be forgotten
                            return "Unable to open \"forgeversion.properties\"";
                        }

                        var iniFile = new IniFile();
                        iniFile.LoadText(forgeVersionData);
                        var major = iniFile.GetString("forge.major.number", "");
                        var minor = iniFile.GetString("forge.minor.number", "");
                        var revision = iniFile.GetString("forge.revision.number", "");
                        var build = iniFile.GetString("forge.build.number", "");

                        if (major.Length == 0 || minor.Length == 0 || revision.Length == 0 || build.Length == 0)
                        {
                            return "Invalid \"forgeversion.properties\"!";
                        }

                        _ = await components.SetComponentVersion("net.minecraftforge",
                            major + '.' + minor + '.' + revision + '.' + build,
                            cancellationToken: cancellationToken);
                    }

                    components.SaveNow();
                    return null;
                }
            }
        }
        else if (File.Exists(versionJson))
        {
            try
            {
                data = Encoding.UTF8.GetString(FileSystem.Read(versionJson));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "Unable to open \"version.json\": " + e.Message;
            }
        }
        else
        {
            // This is the "Vanilla" modpack, excluded by the search code
            _ = await components.SetComponentVersion("net.minecraft", minecraftVersion, important: true,
                cancellationToken: cancellationToken);
            components.SaveNow();
            return null;
        }

        try
        {
            var root = TechnicJson.RequireDocument(data, "version.json");
            JsonUtils.RequireObject(root, "version.json");
            var packMinecraftVersion = StringOrEmpty(root, "inheritsFrom");
            if (packMinecraftVersion.Length == 0)
            {
                if (fmlMinecraftVersion.Length == 0)
                {
                    return "Could not understand \"version.json\":\ninheritsFrom is missing";
                }

                packMinecraftVersion = fmlMinecraftVersion;
            }

            _ = await components.SetComponentVersion("net.minecraft", packMinecraftVersion, important: true,
                cancellationToken: cancellationToken);
            if (root["libraries"] is JsonArray libraries)
            {
                foreach (var library in libraries)
                {
                    if (library is not JsonObject libraryObject)
                    {
                        continue;
                    }

                    var libraryName = StringOrEmpty(libraryObject, "name");

                    if (libraryName.StartsWith("net.neoforged.fancymodloader:", StringComparison.Ordinal))
                    {
                        // it is neoforge
                        // no easy way to get the version from the libs so use the arguments
                        var neoforgeVersion = "";
                        if (root["arguments"] is JsonObject arguments
                            && arguments["game"] is JsonArray game)
                        {
                            var isVersionArg = false;
                            foreach (var arg in game)
                            {
                                var argument = arg is JsonValue value ? StringValue(value) : "";
                                if (isVersionArg)
                                {
                                    neoforgeVersion = argument;
                                    break;
                                }

                                isVersionArg = "--fml.neoForgeVersion" == argument || "--fml.forgeVersion" == argument;
                            }
                        }

                        if (neoforgeVersion.Length != 0)
                        {
                            _ = await components.SetComponentVersion("net.neoforged", neoforgeVersion,
                                cancellationToken: cancellationToken);
                        }

                        break;
                    }

                    if ((libraryName.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal)
                         || libraryName.StartsWith("net.minecraftforge:fmlloader:", StringComparison.Ordinal))
                        && libraryName.Contains('-'))
                    {
                        var libraryVersion = Section(libraryName, ':', 2);
                        if (!libraryVersion.StartsWith("1.7.10-", StringComparison.Ordinal))
                        {
                            _ = await components.SetComponentVersion("net.minecraftforge", Section(libraryName, '-', 1),
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            // 1.7.10 versions sometimes look like 1.7.10-10.13.4.1614-1.7.10, this filters out the 10.13.4.1614 part
                            _ = await components.SetComponentVersion("net.minecraftforge", Section(libraryName, '-', 1, 1),
                                cancellationToken: cancellationToken);
                        }

                        break;
                    }

                    // <Technic library name prefix> -> <our component name>
                    foreach (var (loaderPrefix, componentUid) in LoaderMap)
                    {
                        if (libraryName.StartsWith(loaderPrefix, StringComparison.Ordinal))
                        {
                            _ = await components.SetComponentVersion(componentUid, Section(libraryName, ':', 2),
                                cancellationToken: cancellationToken);
                            break;
                        }
                    }
                }
            }
        }
        catch (JsonParseException e)
        {
            return "Could not understand \"version.json\":\n" + e.Message;
        }

        components.SaveNow();
        return null;
    }

    private static readonly IReadOnlyList<(string Prefix, string Uid)> LoaderMap =
    [
        ("net.minecraftforge:minecraftforge:", "net.minecraftforge"),
        ("net.fabricmc:fabric-loader:", "net.fabricmc.fabric-loader"),
        ("org.quiltmc:quilt-loader:", "org.quiltmc.quilt-loader"),
    ];

    private static string StringOrEmpty(JsonNode? obj, string key)
    {
        return obj is JsonObject map && map[key] is JsonValue value ? StringValue(value) : "";
    }

    private static string StringValue(JsonValue value)
    {
        return value.TryGetValue<string>(out var str) ? str : "";
    }

    /// <summary>QString::section(sep, start, end) for the non-negative ranges used here.</summary>
    private static string Section(string value, char separator, int start, int? end = null)
    {
        var fields = value.Split(separator);
        var last = end ?? fields.Length - 1;
        return string.Join(separator, fields[start..(last + 1)]);
    }

    private static bool ZipEntryExists(ZipArchive zip, string entryName)
    {
        return zip.Entries.Any(entry => NormalizeEntryName(entry.FullName) == entryName);
    }

    private static string? ReadZipEntryText(ZipArchive zip, string entryName)
    {
        // (the C++ goToFile searches on the normalized entry name; null = not found)
        var entry = zip.Entries.FirstOrDefault(e => NormalizeEntryName(e.FullName) == entryName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string NormalizeEntryName(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        while (name.StartsWith('/'))
        {
            name = name[1..];
        }

        return name;
    }
}
