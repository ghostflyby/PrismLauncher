// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/OneSixVersionFormat.{h,cpp}: the Prism/OneSix version JSON.
//
// The QJsonDocument wrapper of versionFileFromJson is replaced by a JsonObject parameter (the
// "document is empty or null" branch cannot trigger for an object); the upstream bug where
// versionFileToJson serializes jarMods into the "mods" array is preserved and marked.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Meta;

namespace FlintLauncher.Core.Minecraft;

public static partial class OneSixVersionFormat
{
    /// <summary>The probe read over a JsonObject: shape failures keep the "{what}: Error
    /// parsing JSON: ..." wrapper family.</summary>
    private static T Probe<T>(JsonNode node, string what, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(node, typeInfo)
                   ?? throw new JsonParseException(what + ": Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException(what + ": Error parsing JSON: " + e.Message);
        }
    }

    public static Library LibraryFromJson(ProblemContainer problems, JsonObject libObj, string filename)
    {
        var dto = Probe(libObj, filename, VersionFormatJsonContext.Default.LibraryJson);
        return MapLib(dto, problems, filename);
    }

    /// <summary>The OneSix library mapping: the Mojang half plus the MMC extension keys.</summary>
    private static Library MapLib(LibraryJson dto, ProblemContainer problems, string filename)
    {
        var outLib = VersionFormatMapper.MapLibrary(dto, problems, filename);
        VersionFormatMapper.MapMmcLibrary(dto, outLib);
        return outLib;
    }

    public static JsonObject LibraryToJson(Library library)
    {
        var libRoot = MojangVersionFormat.LibraryToJson(library);
        if (library.AbsoluteUrl.Length != 0)
        {
            libRoot["MMC-absoluteUrl"] = library.AbsoluteUrl;
        }

        if (library.Hint.Length != 0)
        {
            libRoot["MMC-hint"] = library.Hint;
        }

        if (library.FilenameOverride.Length != 0)
        {
            libRoot["MMC-filename"] = library.FilenameOverride;
        }

        if (library.DisplayNameValue.Length != 0)
        {
            libRoot["MMC-displayname"] = library.DisplayNameValue;
        }

        return libRoot;
    }

    [GeneratedRegex(@"\A(?:[a-zA-Z0-9\-_]+(?:\.[a-zA-Z0-9\-_]+)*)\z")]
    private static partial Regex ValidUidRegex();

    public static VersionFile VersionFileFromJson(JsonObject root, string filename, bool requireOrder)
    {
        var outInfo = new VersionFile();

        var formatVersion = MetaJsonFormat.ParseFormatVersion(root, false);
        switch (formatVersion)
        {
            case MetadataVersion.InitialRelease:
                break;
            case MetadataVersion.Invalid:
                throw new JsonParseException(filename + " does not contain a recognizable version of the metadata format.");
        }

        var dto = Probe(root, filename, VersionFormatJsonContext.Default.VersionFileJson);

        if (requireOrder)
        {
            if (dto.Order is { } order)
            {
                outInfo.Order = order;
            }
            // FIXME upstream: evaluate if we don't want to throw exceptions here instead
            // qCritical() << filename << "doesn't contain an order field";
        }

        outInfo.Name = dto.Name ?? "";

        outInfo.Uid = dto.Uid ?? dto.FileId ?? "";

        if (!ValidUidRegex().IsMatch(outInfo.Uid))
        {
            // qCritical() << "The component's 'uid' contains illegal characters! UID:" << outInfo.Uid;
            outInfo.AddProblem(ProblemSeverity.Error,
                "The component's 'uid' contains illegal characters! This can cause security issues.");
        }

        outInfo.Version = dto.Version ?? "";

        VersionFormatMapper.MapVersionProperties(dto, outInfo);

        // added for legacy Minecraft window embedding, TODO: remove
        if (dto.AppletClass is not null)
        {
            outInfo.AppletClass = dto.AppletClass;
        }

        if (dto.AddTweakers is { } tweakers)
        {
            outInfo.AddTweakers.AddRange(tweakers);
        }

        if (dto.AddTraits is { } traits)
        {
            foreach (var trait in traits)
            {
                outInfo.Traits.Add(trait);
            }
        }

        if (dto.AddJvmArgs is { } jvmArgs)
        {
            outInfo.AddnJvmArguments.AddRange(jvmArgs);
        }

        if (dto.JarMods is { } jarMods)
        {
            foreach (var libDto in jarMods)
            {
                // parse the jarmod and add to jar mods
                outInfo.JarMods.Add(MapLib(libDto, outInfo, filename));
            }
        }
        else if (dto.PlusJarMods is { } plusJarMods) // DEPRECATED: old style, backwards compatibility only
        {
            foreach (var entry in plusJarMods)
            {
                var parts = VersionFormatMapper.MapPlusJarMod(entry, filename, outInfo.Name);
                // just make up something unique on the spot for the library name.
                var lib = new Library
                {
                    Name = new GradleSpecifier("org.multimc.jarmods:" + Guid.NewGuid().ToString() + ":1"),
                    // the filename override is the old name; it needs to be local, it is
                    // stored in the instance jarmods folder
                    FilenameOverride = parts.FilenameOverride,
                    Hint = "local",
                    DisplayNameValue = parts.DisplayName,
                };
                outInfo.JarMods.Add(lib);
            }
        }

        if (dto.Mods is { } mods)
        {
            foreach (var libDto in mods)
            {
                outInfo.Mods.Add(MapLib(libDto, outInfo, filename));
            }
        }

        var hasPlusLibs = dto.PlusLibraries is not null;
        var hasLibs = dto.Libraries is not null;
        if (hasPlusLibs && hasLibs)
        {
            outInfo.AddProblem(ProblemSeverity.Warning,
                "Version file has both '+libraries' and 'libraries'. This is no longer supported.");
        }

        if (hasLibs)
        {
            foreach (var libDto in dto.Libraries!)
            {
                outInfo.Libraries.Add(MapLib(libDto, outInfo, filename));
            }
        }

        if (hasPlusLibs)
        {
            foreach (var libDto in dto.PlusLibraries!)
            {
                outInfo.Libraries.Add(MapLib(libDto, outInfo, filename));
            }
        }

        if (dto.MavenFiles is { } mavenFiles)
        {
            foreach (var libDto in mavenFiles)
            {
                outInfo.MavenFiles.Add(MapLib(libDto, outInfo, filename));
            }
        }

        if (dto.Agents is { } agents)
        {
            foreach (var agentDto in agents)
            {
                var lib = MapLib(agentDto, outInfo, filename);
                outInfo.Agents.Add(new Agent(lib, agentDto.Argument ?? ""));
            }
        }

        // if we have mainJar, just use it
        if (dto.MainJar is { } mainJarDto)
        {
            outInfo.MainJar = MapLib(mainJarDto, outInfo, filename);
        }
        // else reconstruct it from downloads and id ... if that's available
        else if (outInfo.MinecraftVersion.Length != 0)
        {
            var lib = new Library
            {
                Name = new GradleSpecifier($"com.mojang:minecraft:{outInfo.MinecraftVersion}:client")
            };
            // we have a reliable client download, use it.
            if (outInfo.MojangDownloads.TryGetValue("client", out var clientDownload))
            {
                var libDlInfo = new MojangLibraryDownloadInfo
                {
                    Artifact = clientDownload,
                };
                lib.MojangDownloads = libDlInfo;
            }
            // we got nothing...
            else
            {
                outInfo.AddProblem(ProblemSeverity.Error,
                    "URL for the main jar could not be determined - Mojang removed the server that we used as fallback.");
            }

            outInfo.MainJar = lib;
        }

        if (root.Contains("requires"))
        {
            MetaJsonFormat.ParseRequires(root, outInfo.Requires);
        }

        if (dto.McVersion is { Length: > 0 } dependsOnMinecraftVersion)
        {
            var mcReq = new Require
            {
                Uid = "net.minecraft",
                EqualsVersion = dependsOnMinecraftVersion,
            };
            if (!outInfo.Requires.Contains(mcReq))
            {
                outInfo.Requires.Add(mcReq);
            }
        }

        if (root.Contains("conflicts"))
        {
            MetaJsonFormat.ParseRequires(root, outInfo.Conflicts, "conflicts");
        }

        if (dto.Volatile is { } volatileFlag)
        {
            outInfo.Volatile = volatileFlag;
        }

        if (dto.Runtimes is { } runtimes)
        {
            outInfo.Runtimes = [];
            foreach (var runtime in runtimes)
            {
                outInfo.Runtimes.Add(VersionFormatMapper.MapJavaRuntime(runtime));
            }
        }

        /* removed features that shouldn't be used (QJsonObject::contains semantics: the
           member counts even when it holds JSON null, so probe the node, not the DTO) */
        if (root.Contains("tweakers"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element 'tweakers'");
        }

        if (root.Contains("-libraries"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '-libraries'");
        }

        if (root.Contains("-tweakers"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '-tweakers'");
        }

        if (root.Contains("-minecraftArguments"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '-minecraftArguments'");
        }

        if (root.Contains("+minecraftArguments"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '+minecraftArguments'");
        }

        return outInfo;
    }

    /// <summary>Port of OneSixVersionFormat::versionFileToJson (QJsonDocument -> JsonObject).</summary>
    public static JsonObject VersionFileToJson(VersionFile patch)
    {
        var root = new JsonObject();
        JsonUtils.WriteString(root, "name", patch.Name);

        JsonUtils.WriteString(root, "uid", patch.Uid);

        JsonUtils.WriteString(root, "version", patch.Version);

        MetaJsonFormat.SerializeFormatVersion(root, MetadataVersion.InitialRelease);

        MojangVersionFormat.WriteVersionProperties(patch, root);

        if (patch.MainJar is not null)
        {
            root["mainJar"] = LibraryToJson(patch.MainJar);
        }

        JsonUtils.WriteString(root, "appletClass", patch.AppletClass);
        JsonUtils.WriteStringList(root, "+tweakers", patch.AddTweakers);
        // QSet iteration order is unspecified upstream; a sorted list keeps the output deterministic
        JsonUtils.WriteStringList(root, "+traits", [.. patch.Traits.OrderBy(t => t, StringComparer.Ordinal)]);
        JsonUtils.WriteStringList(root, "+jvmArgs", patch.AddnJvmArguments);
        if (patch.Agents.Count != 0)
        {
            var array = new JsonArray();
            foreach (var value in patch.Agents)
            {
                var agentOut = LibraryToJson(value.Library);
                if (value.Argument.Length != 0)
                {
                    agentOut["argument"] = value.Argument;
                }

                array.Add((JsonNode)agentOut);
            }

            root["+agents"] = array;
        }

        if (patch.Libraries.Count != 0)
        {
            var array = new JsonArray();
            foreach (var value in patch.Libraries)
            {
                array.Add((JsonNode)LibraryToJson(value));
            }

            root["libraries"] = array;
        }

        if (patch.MavenFiles.Count != 0)
        {
            var array = new JsonArray();
            foreach (var value in patch.MavenFiles)
            {
                array.Add((JsonNode)LibraryToJson(value));
            }

            root["mavenFiles"] = array;
        }

        if (patch.JarMods.Count != 0)
        {
            var array = new JsonArray();
            foreach (var value in patch.JarMods)
            {
                array.Add((JsonNode)JarModToJson(value));
            }

            root["jarMods"] = array;
        }

        if (patch.Mods.Count != 0)
        {
            var array = new JsonArray();
            // NOTE: upstream iterates patch->jarMods here — an obvious bug, kept for fidelity
            foreach (var value in patch.JarMods)
            {
                array.Add((JsonNode)ModToJson(value));
            }

            root["mods"] = array;
        }

        if (patch.Requires.Count == 0)
        {
            MetaJsonFormat.SerializeRequires(root, patch.Requires);
        }

        if (patch.Conflicts.Count == 0)
        {
            MetaJsonFormat.SerializeRequires(root, patch.Conflicts, "conflicts");
        }

        if (patch.Volatile)
        {
            root["volatile"] = true;
        }

        return root;
    }

    public static Library PlusJarModFromJson(
        ProblemContainer problems,
        JsonObject libObj,
        string filename,
        string originalName)
    {
        var dto = Probe(libObj, filename, VersionFormatJsonContext.Default.PlusJarModJson);
        var parts = VersionFormatMapper.MapPlusJarMod(dto, filename, originalName);

        // just make up something unique on the spot for the library name.
        var outLib = new Library
        {
            Name = new GradleSpecifier("org.multimc.jarmods:" + Guid.NewGuid().ToString() + ":1"),
            // the filename override is the old name; it needs to be local, it is stored in
            // the instance jarmods folder
            FilenameOverride = parts.FilenameOverride,
            Hint = "local",
            DisplayNameValue = parts.DisplayName,
        };
        return outLib;
    }

    public static Library JarModFromJson(ProblemContainer problems, JsonObject libObj, string filename)
    {
        return LibraryFromJson(problems, libObj, filename);
    }

    public static JsonObject JarModToJson(Library jarmod)
    {
        return LibraryToJson(jarmod);
    }

    public static Library ModFromJson(ProblemContainer problems, JsonObject libObj, string filename)
    {
        return LibraryFromJson(problems, libObj, filename);
    }

    public static JsonObject ModToJson(Library jarmod)
    {
        return LibraryToJson(jarmod);
    }
}
