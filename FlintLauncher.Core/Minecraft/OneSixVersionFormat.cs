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
using FlintLauncher.Core.Meta;

namespace FlintLauncher.Core.Minecraft;

public static partial class OneSixVersionFormat
{
    /// <summary>The probe read over a JsonObject: shape failures keep the "{what}: Error
    /// parsing JSON: ..." wrapper family.</summary>
    private static T Probe<T>(JsonNode node, string what, JsonTypeInfo<T> typeInfo) where T : class
    {
        T? dto;
        try
        {
            dto = node.Deserialize(typeInfo);
        }
        catch (JsonException e)
        {
            throw new JsonException(what + ": Error parsing JSON: " + e.Message);
        }

        return dto ?? throw new JsonException(what + ": Error parsing JSON: document is null");
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

    internal static LibraryOutJson LibraryToJson(Library library)
    {
        var wire = MojangVersionFormat.LibraryToJson(library);
        if (library.AbsoluteUrl.Length != 0 || library.Hint.Length != 0 ||
            library.FilenameOverride.Length != 0 || library.DisplayNameValue.Length != 0)
        {
            wire = wire with
            {
                MmcAbsoluteUrl = library.AbsoluteUrl.Length != 0 ? library.AbsoluteUrl : null,
                MmcHint = library.Hint.Length != 0 ? library.Hint : null,
                MmcFilename = library.FilenameOverride.Length != 0 ? library.FilenameOverride : null,
                MmcDisplayname = library.DisplayNameValue.Length != 0 ? library.DisplayNameValue : null,
            };
        }

        return wire;
    }

    [GeneratedRegex(@"\A(?:[a-zA-Z0-9\-_]+(?:\.[a-zA-Z0-9\-_]+)*)\z")]
    private static partial Regex ValidUidRegex();

    public static VersionFile VersionFileFromJson(JsonObject root, string filename, bool requireOrder)
    {
        var outInfo = new VersionFile();

        var dto = Probe(root, filename, VersionFormatJsonContext.Default.VersionFileJson);

        var formatVersion = MetaJsonFormat.ParseFormatVersion(dto.FormatVersion, false);
        switch (formatVersion)
        {
            case MetadataVersion.InitialRelease:
                break;
            case MetadataVersion.Invalid:
                throw new JsonException(filename + " does not contain a recognizable version of the metadata format.");
        }

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

        if (dto.Libraries is { } libraries)
        {
            foreach (var libDto in libraries)
            {
                outInfo.Libraries.Add(MapLib(libDto, outInfo, filename));
            }
        }

        if (dto.PlusLibraries is { } plusLibraries)
        {
            foreach (var libDto in plusLibraries)
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

        MetaJsonFormat.ParseRequires(dto.Requires, outInfo.Requires);

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

        MetaJsonFormat.ParseRequires(dto.Conflicts, outInfo.Conflicts, "conflicts");

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
        if (root.ContainsKey("tweakers"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element 'tweakers'");
        }

        if (root.ContainsKey("-libraries"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '-libraries'");
        }

        if (root.ContainsKey("-tweakers"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '-tweakers'");
        }

        if (root.ContainsKey("-minecraftArguments"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '-minecraftArguments'");
        }

        if (root.ContainsKey("+minecraftArguments"))
        {
            outInfo.AddProblem(ProblemSeverity.Error, "Version file contains unsupported element '+minecraftArguments'");
        }

        return outInfo;
    }

    /// <summary>Port of OneSixVersionFormat::versionFileToJson, as the typed write wire.
    /// NOTE preserved upstream quirk: the "mods" array iterates the JARMODS (an obvious
    /// upstream bug), and requires/conflicts are never written (the former node write's
    /// inverted emptiness check made SerializeRequires a no-op in every case).</summary>
    internal static VersionFileOutJson VersionFileToJson(VersionFile patch)
    {
        var wire = new VersionFileOutJson
        {
            Name = patch.Name.Length != 0 ? patch.Name : null,
            Uid = patch.Uid.Length != 0 ? patch.Uid : null,
            Version = patch.Version.Length != 0 ? patch.Version : null,
            FormatVersion = (int)MetadataVersion.InitialRelease,
            MainJar = patch.MainJar is { } mainJar ? LibraryToJson(mainJar) : null,
            AppletClass = patch.AppletClass.Length != 0 ? patch.AppletClass : null,
            AddTweakers = patch.AddTweakers.Count != 0 ? [.. patch.AddTweakers] : null,
            // QSet iteration order is unspecified upstream; a sorted list keeps the output deterministic
            AddTraits = patch.Traits.Count != 0
                ? [.. patch.Traits.OrderBy(t => t, StringComparer.Ordinal)]
                : null,
            AddJvmArgs = patch.AddnJvmArguments.Count != 0 ? [.. patch.AddnJvmArguments] : null,
            Agents = patch.Agents.Count != 0
                ? patch.Agents.Select(agent => agent.Argument.Length != 0
                    ? LibraryToJson(agent.Library) with { Argument = agent.Argument }
                    : LibraryToJson(agent.Library)).ToList()
                : null,
            Libraries = patch.Libraries.Count != 0 ? patch.Libraries.Select(LibraryToJson).ToList() : null,
            MavenFiles = patch.MavenFiles.Count != 0 ? patch.MavenFiles.Select(LibraryToJson).ToList() : null,
            JarMods = patch.JarMods.Count != 0 ? patch.JarMods.Select(JarModToJson).ToList() : null,
            // NOTE: upstream iterates patch->jarMods here — an obvious bug, kept for
            // fidelity INCLUDING its gate: the array exists only when the MODS list is
            // non-empty, so a jar-mod-only patch writes no "mods" member (and a save/reload
            // cycle does not turn jar mods into mods)
            Mods = patch.Mods.Count != 0 ? patch.JarMods.Select(ModToJson).ToList() : null,
            Volatile = patch.Volatile ? true : null,
        };
        return MergeMojangHalf(wire, MojangVersionFormat.WriteVersionProperties(patch));
    }

    /// <summary>Folds the Mojang member half into the OneSix wire (the former single
    /// WriteVersionProperties pass over the same JsonObject).</summary>
    private static VersionFileOutJson MergeMojangHalf(VersionFileOutJson wire, VersionFileOutJson mojangHalf) =>
        wire with
        {
            Id = mojangHalf.Id,
            MainClass = mojangHalf.MainClass,
            MinecraftArguments = mojangHalf.MinecraftArguments,
            Type = mojangHalf.Type,
            ReleaseTime = mojangHalf.ReleaseTime,
            Time = mojangHalf.Time,
            MinimumLauncherVersion = mojangHalf.MinimumLauncherVersion,
            Assets = mojangHalf.Assets,
            AssetIndex = mojangHalf.AssetIndex,
            Downloads = mojangHalf.Downloads,
            CompatibleJavaMajors = mojangHalf.CompatibleJavaMajors,
            CompatibleJavaName = mojangHalf.CompatibleJavaName,
        };

    /// <summary>The serialized patch text for the FileOps.Write call sites (the former
    /// QtJson.ToIndentedString(VersionFileToJson(...)) + "\n"; the typed write shape is
    /// standard indented JSON).</summary>
    internal static string SerializeVersionFile(VersionFile patch)
    {
        return JsonSerializer.Serialize(VersionFileToJson(patch), VersionFormatJsonContext.Default.VersionFileOutJson);
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

    internal static LibraryOutJson JarModToJson(Library jarmod)
    {
        return LibraryToJson(jarmod);
    }

    public static Library ModFromJson(ProblemContainer problems, JsonObject libObj, string filename)
    {
        return LibraryFromJson(problems, libObj, filename);
    }

    internal static LibraryOutJson ModToJson(Library jarmod)
    {
        return LibraryToJson(jarmod);
    }
}
