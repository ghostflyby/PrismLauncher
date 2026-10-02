// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/OneSixVersionFormat.{h,cpp}: the Prism/OneSix version JSON.
//
// The QJsonDocument wrapper of versionFileFromJson is replaced by a JsonObject parameter (the
// "document is empty or null" branch cannot trigger for an object); the upstream bug where
// versionFileToJson serializes jarMods into the "mods" array is preserved and marked.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Meta;

namespace FlintLauncher.Core.Minecraft;

public static partial class OneSixVersionFormat
{
    private static string ReadString(JsonObject root, string key, string current)
    {
        return root.Contains(key) ? JsonUtils.RequireString(root, key, "Value") : current;
    }

    /// <summary>root.value(key) as a node (for the require*(JsonNode) overload shape).</summary>
    private static JsonNode? ValueOf(JsonObject root, string key)
    {
        return root[key];
    }

    public static Library LibraryFromJson(ProblemContainer problems, JsonObject libObj, string filename)
    {
        var outLib = MojangVersionFormat.LibraryFromJson(problems, libObj, filename);
        outLib.Hint = ReadString(libObj, "MMC-hint", outLib.Hint);
        outLib.AbsoluteUrl = ReadString(libObj, "MMC-absulute_url", outLib.AbsoluteUrl);
        outLib.AbsoluteUrl = ReadString(libObj, "MMC-absoluteUrl", outLib.AbsoluteUrl);
        outLib.FilenameOverride = ReadString(libObj, "MMC-filename", outLib.FilenameOverride);
        outLib.DisplayNameValue = ReadString(libObj, "MMC-displayname", outLib.DisplayNameValue);
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

        if (requireOrder)
        {
            if (root.Contains("order"))
            {
                outInfo.Order = JsonUtils.RequireInteger(root, "order", "Value");
            }
            else
            {
                // FIXME upstream: evaluate if we don't want to throw exceptions here instead
                // qCritical() << filename << "doesn't contain an order field";
            }
        }

        outInfo.Name = root.StringOr("name");

        if (root.Contains("uid"))
        {
            outInfo.Uid = root.StringOr("uid");
        }
        else
        {
            outInfo.Uid = root.StringOr("fileId");
        }

        if (!ValidUidRegex().IsMatch(outInfo.Uid))
        {
            // qCritical() << "The component's 'uid' contains illegal characters! UID:" << outInfo.Uid;
            outInfo.AddProblem(ProblemSeverity.Error,
                "The component's 'uid' contains illegal characters! This can cause security issues.");
        }

        outInfo.Version = root.StringOr("version");

        MojangVersionFormat.ReadVersionProperties(root, outInfo);

        // added for legacy Minecraft window embedding, TODO: remove
        outInfo.AppletClass = ReadString(root, "appletClass", outInfo.AppletClass);

        if (root.Contains("+tweakers"))
        {
            foreach (var tweakerVal in JsonUtils.RequireArray(ValueOf(root, "+tweakers")))
            {
                outInfo.AddTweakers.Add(JsonUtils.RequireString(tweakerVal));
            }
        }

        if (root.Contains("+traits"))
        {
            foreach (var traitVal in JsonUtils.RequireArray(ValueOf(root, "+traits")))
            {
                outInfo.Traits.Add(JsonUtils.RequireString(traitVal));
            }
        }

        if (root.Contains("+jvmArgs"))
        {
            foreach (var arg in JsonUtils.RequireArray(ValueOf(root, "+jvmArgs")))
            {
                outInfo.AddnJvmArguments.Add(JsonUtils.RequireString(arg));
            }
        }

        if (root.Contains("jarMods"))
        {
            foreach (var libVal in JsonUtils.RequireArray(ValueOf(root, "jarMods")))
            {
                var libObj = JsonUtils.RequireObject(libVal);
                // parse the jarmod
                var lib = JarModFromJson(outInfo, libObj, filename);
                // and add to jar mods
                outInfo.JarMods.Add(lib);
            }
        }
        else if (root.Contains("+jarMods")) // DEPRECATED: old style '+jarMods' are only here for backwards compatibility
        {
            foreach (var libVal in JsonUtils.RequireArray(ValueOf(root, "+jarMods")))
            {
                var libObj = JsonUtils.RequireObject(libVal);
                // parse the jarmod
                var lib = PlusJarModFromJson(outInfo, libObj, filename, outInfo.Name);
                // and add to jar mods
                outInfo.JarMods.Add(lib);
            }
        }

        if (root.Contains("mods"))
        {
            foreach (var libVal in JsonUtils.RequireArray(ValueOf(root, "mods")))
            {
                var libObj = JsonUtils.RequireObject(libVal);
                // parse the mod
                var lib = ModFromJson(outInfo, libObj, filename);
                // and add to mods
                outInfo.Mods.Add(lib);
            }
        }

        void ReadLibs(string which, List<Library> outList)
        {
            foreach (var libVal in JsonUtils.RequireArray(ValueOf(root, which)))
            {
                var libObj = JsonUtils.RequireObject(libVal);
                // parse the library
                var lib = LibraryFromJson(outInfo, libObj, filename);
                outList.Add(lib);
            }
        }

        var hasPlusLibs = root.Contains("+libraries");
        var hasLibs = root.Contains("libraries");
        if (hasPlusLibs && hasLibs)
        {
            outInfo.AddProblem(ProblemSeverity.Warning,
                "Version file has both '+libraries' and 'libraries'. This is no longer supported.");
            ReadLibs("libraries", outInfo.Libraries);
            ReadLibs("+libraries", outInfo.Libraries);
        }
        else if (hasLibs)
        {
            ReadLibs("libraries", outInfo.Libraries);
        }
        else if (hasPlusLibs)
        {
            ReadLibs("+libraries", outInfo.Libraries);
        }

        if (root.Contains("mavenFiles"))
        {
            ReadLibs("mavenFiles", outInfo.MavenFiles);
        }

        if (root.Contains("+agents"))
        {
            foreach (var agentVal in JsonUtils.RequireArray(ValueOf(root, "+agents")))
            {
                var agentObj = JsonUtils.RequireObject(agentVal);
                var lib = LibraryFromJson(outInfo, agentObj, filename);

                var arg = "";
                arg = ReadString(agentObj, "argument", arg);

                outInfo.Agents.Add(new Agent(lib, arg));
            }
        }

        // if we have mainJar, just use it
        if (root.Contains("mainJar"))
        {
            var libObj = JsonUtils.RequireObject(root, "mainJar");
            outInfo.MainJar = LibraryFromJson(outInfo, libObj, filename);
        }
        // else reconstruct it from downloads and id ... if that's available
        else if (outInfo.MinecraftVersion.Length != 0)
        {
            var lib = new Library();
            lib.SetRawName(new GradleSpecifier($"com.mojang:minecraft:{outInfo.MinecraftVersion}:client"));
            // we have a reliable client download, use it.
            if (outInfo.MojangDownloads.TryGetValue("client", out var clientDownload))
            {
                var libDlInfo = new MojangLibraryDownloadInfo
                {
                    Artifact = clientDownload,
                };
                lib.SetMojangDownloadInfo(libDlInfo);
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
        var dependsOnMinecraftVersion = root.StringOr("mcVersion");
        if (dependsOnMinecraftVersion.Length != 0)
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
        if (root.Contains("volatile"))
        {
            outInfo.Volatile = JsonUtils.RequireBoolean(root, "volatile", "Value");
        }

        if (root.Contains("runtimes"))
        {
            outInfo.Runtimes = [];
            foreach (var runtime in JsonUtils.RequireArray(ValueOf(root, "runtimes")))
            {
                outInfo.Runtimes.Add(JavaMetadata.ParseJavaMeta(JsonUtils.RequireObject(runtime)));
            }
        }

        /* removed features that shouldn't be used */
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
        if (!patch.Requires.IsEmpty)
        {
            MetaJsonFormat.SerializeRequires(root, patch.Requires, "requires");
        }
        if (!patch.Conflicts.IsEmpty)
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
        var outLib = new Library();
        if (!libObj.Contains("name"))
        {
            throw new JsonParseException(filename + "contains a jarmod that doesn't have a 'name' field");
        }

        // just make up something unique on the spot for the library name.
        var id = Guid.NewGuid().ToString(); // QUuid::createUuid().toString(QUuid::WithoutBraces)
        outLib.SetRawName(new GradleSpecifier("org.multimc.jarmods:" + id + ":1"));

        // filename override is the old name
        outLib.SetFilename(libObj.StringOr("name"));

        // it needs to be local, it is stored in the instance jarmods folder
        outLib.SetHint("local");

        // read the original name if present - some versions did not set it
        // it is the original jar mod filename before it got renamed at the point of addition
        var displayName = libObj.StringOr("originalName");
        if (displayName.Length == 0)
        {
            var fixedName = originalName.Replace(" (jar mod)", "");
            outLib.SetDisplayName(fixedName);
        }
        else
        {
            outLib.SetDisplayName(displayName);
        }
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
