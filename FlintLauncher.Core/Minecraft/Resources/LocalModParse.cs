// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/tasks/LocalModParseTask.{h,cpp} (the ModUtils namespace):
// the mod-metadata readers for fabric.mod.json, quilt.mod.json, META-INF/mods.toml (and
// neoforge.mods.toml), mcmod.info, forgeversion.properties, litemod.json and nilmod.css
// (through the qdcss comment-embedded-properties parser), plus the archive/folder
// processing dispatch that turns a mod file into ModDetails.
//
// Deviations from the C++:
//  - LocalModParseTask (the Task/abort plumbing) is not ported; callers run the synchronous
//    Process* calls and fill ModDetails directly (see ModFolderModel.Update).
//  - processIconPNG/loadIconFile (the QPixmap icon extraction) are not ported — the icon is
//    kept data-level as the inner archive path on ModDetails.IconFile.
//  - Zip inspection uses System.IO.Compression (like MMCZip.cs) instead of the ArchiveReader;
//    the parse-callback loop is mirrored with a plain entry iteration and a stop flag.
//  - The INIFile (QSettings IniFormat) reader of ReadForgeInfo is approximated with a minimal
//    "key=value" properties parse (sections/comments skipped); forgeversion.properties only
//    needs the flat forge.*.*.number keys.
//  - The toml++ read of ReadMCModTOML goes through MiniToml (extended with [[array of
//    tables]] for [[mods]] / [[dependencies.<modId>]]); a parse failure yields empty details
//    like the caught toml++ parse_error.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.ModPlatform.Packwiz;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of ModUtils::ProcessingLevel.</summary>
public enum ModProcessingLevel
{
    Full,
    BasicInfoOnly,
}

/// <summary>
/// Port of the ModUtils namespace: best-effort mod metadata parsing. Failures leave
/// filename-only details (the C++ parse is deliberately non-fatal for the folder model).
/// </summary>
public static class ModUtils
{
    private static readonly Regex NewlineRegex = new("\r\n|\n|\r");
    private static readonly Regex QdcssRulesetRegex = new("[#.]?(@?\\w+?)\\s*\\{(.*?)\\}",
        RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    private static readonly Regex QdcssRuleRegex = new(
        "(\\S+?)\\s*:\\s*(?:\"(.*?)(?<!\\\\)\"|'(.*?)(?<!\\\\)'|(\\S+?))\\s*(?:;|$)",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    // ------------------------------------------------------------------ dispatch

    /// <summary>Port of ModUtils::process (dispatch on the mod type).</summary>
    public static bool Process(Mod mod, ModProcessingLevel level = ModProcessingLevel.Full)
    {
        switch (mod.Type)
        {
            case ResourceType.Folder:
                return ProcessFolder(mod, level);
            case ResourceType.ZipFile:
                return ProcessZip(mod, level);
            case ResourceType.LiteMod:
                return ProcessLitemod(mod);
            default:
                // qWarning() << "Invalid type" << mod.type() << "for mod parse task!";
                return false;
        }
    }

    /// <summary>Port of ModUtils::processZIP: scan the archive for the metadata candidates,
    /// in entry order, honouring the C++ stop rules (the forge jar-version dance between
    /// mods.toml and MANIFEST.MF, and the nilloader canary/meta pair).</summary>
    public static bool ProcessZip(Mod mod, ModProcessingLevel level = ModProcessingLevel.Full)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(mod.Fileinfo.FullName);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return false; // can't open the archive
        }

        using (zip)
        {
            var details = new ModDetails();

            var baseForgePopulated = false;
            var isNilMod = false;
            var isValid = false;
            var stop = false;
            string manifestVersion = "";
            byte[]? nilData = null;
            string nilFilePath = "";

            foreach (var entry in zip.Entries)
            {
                var filePath = entry.FullName;

                if (filePath == "META-INF/mods.toml" || filePath == "META-INF/neoforge.mods.toml")
                {
                    details = ReadMcModToml(Encoding.UTF8.GetString(ReadEntry(entry)));
                    isValid = true;
                    if (details.Version == "${file.jarVersion}" && manifestVersion.Length != 0)
                    {
                        details.Version = manifestVersion;
                    }
                    stop = details.Version != "${file.jarVersion}";
                    baseForgePopulated = true;
                    if (stop)
                    {
                        break;
                    }
                    continue;
                }
                if (filePath == "META-INF/MANIFEST.MF")
                {
                    // quick and dirty line-by-line parser
                    var manifestLines = NewlineRegex.Split(Encoding.UTF8.GetString(ReadEntry(entry)));
                    manifestVersion = "";
                    foreach (var line in manifestLines)
                    {
                        if (line.StartsWith("Implementation-Version: ", StringComparison.OrdinalIgnoreCase))
                        {
                            manifestVersion = RemoveIgnoreCase(line, "Implementation-Version: ");
                            break;
                        }
                    }

                    // some mods use ${projectversion} in their build.gradle, causing this mess to show up in MANIFEST.MF
                    // also keep with forge's behavior of setting the version to "NONE" if none is found
                    if (manifestVersion.Contains("task ':jar' property 'archiveVersion'", StringComparison.Ordinal) || manifestVersion.Length == 0)
                    {
                        manifestVersion = "NONE";
                    }
                    if (baseForgePopulated)
                    {
                        details.Version = manifestVersion;
                        stop = true;
                    }
                    if (stop)
                    {
                        break;
                    }
                    continue;
                }
                if (filePath == "mcmod.info")
                {
                    details = ReadMcModInfo(Encoding.UTF8.GetString(ReadEntry(entry)));
                    isValid = true;
                    break; // stop
                }
                if (filePath == "quilt.mod.json")
                {
                    details = ReadQuiltModInfo(Encoding.UTF8.GetString(ReadEntry(entry)));
                    isValid = true;
                    break; // stop
                }
                if (filePath == "fabric.mod.json")
                {
                    details = ReadFabricModInfo(Encoding.UTF8.GetString(ReadEntry(entry)));
                    isValid = true;
                    break; // stop
                }
                if (filePath == "forgeversion.properties")
                {
                    details = ReadForgeInfo(Encoding.UTF8.GetString(ReadEntry(entry)));
                    isValid = true;
                    break; // stop
                }
                if (filePath == "META-INF/nil/mappings.json")
                {
                    // nilloader uses the filename of the metadata file for the modid, so we can't know the exact filename
                    // thankfully, there is a good file to use as a canary so we don't look for nil meta all the time
                    isNilMod = true;
                    if (nilFilePath.Length != 0)
                    {
                        break; // stop
                    }
                    continue; // file->skip()
                }
                // nilmods can shade nilloader to be able to run as a standalone agent - which includes nilloader's own meta file
                if (filePath.EndsWith(".nilmod.css", StringComparison.Ordinal) && filePath != "nilloader.nilmod.css")
                {
                    nilData = ReadEntry(entry);
                    nilFilePath = filePath;
                    if (isNilMod)
                    {
                        break; // stop
                    }
                    continue;
                }
                // file->skip()
            }

            if (isNilMod)
            {
                details = ReadNilModInfo(nilData is null ? "" : Encoding.UTF8.GetString(nilData), nilFilePath);
                isValid = true;
            }

            if (isValid)
            {
                mod.SetDetails(details);
                return true;
            }

            return false; // no valid mod found in archive
        }
    }

    /// <summary>Port of ModUtils::processFolder (mcmod.info in the folder root).</summary>
    public static bool ProcessFolder(Mod mod, ModProcessingLevel level = ModProcessingLevel.Full)
    {
        var mcmodInfoPath = FileSystem.PathCombine(mod.Fileinfo.FullName, "mcmod.info");
        var mcmodInfo = new FileInfo(mcmodInfoPath);
        if (mcmodInfo.Exists && !mcmodInfo.Attributes.HasFlag(FileAttributes.Directory))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(mcmodInfo.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException)
            {
                return false; // can't open mcmod.info
            }

            if (data.Length == 0)
            {
                return false;
            }

            var details = ReadMcModInfo(Encoding.UTF8.GetString(data));

            mod.SetDetails(details);
            return true;
        }

        return false; // no valid mcmod.info file found
    }

    /// <summary>Port of ModUtils::processLitemod.</summary>
    public static bool ProcessLitemod(Mod mod)
    {
        try
        {
            using var zip = ZipFile.OpenRead(mod.Fileinfo.FullName);
            var file = zip.GetEntry("litemod.json");
            if (file is null)
            {
                return false; // no valid litemod.json found in archive
            }

            var details = ReadLiteModInfo(Encoding.UTF8.GetString(ReadEntry(file)));

            mod.SetDetails(details);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return false; // can't open the archive
        }
    }

    /// <summary>Port of ModUtils::validate: whether a file is valid as a mod or not.</summary>
    public static bool Validate(string file)
    {
        var mod = new Mod(file);
        return Process(mod, ModProcessingLevel.BasicInfoOnly) && mod.Valid;
    }

    // ------------------------------------------------------------------ readers

    // NEW format
    // https://github.com/MinecraftForge/FML/wiki/FML-mod-information-file/c8d8f1929aff9979e322af79a59ce81f3e02db6a

    // OLD format:
    // https://github.com/MinecraftForge/FML/wiki/FML-mod-information-file/5bf6a2d05145ec79387acc0d45c958642fb049fc
    public static ModDetails ReadMcModInfo(string contents)
    {
        var root = ParseJson(contents);

        if (root is JsonArray array)
        {
            // this is the very old format that had just the array
            return GetInfoFromArray(array);
        }

        if (root is JsonObject obj)
        {
            var versionNode = obj.Contains("modinfoversion") ? obj["modinfoversion"] : obj["modinfoversion"].NullOr(obj["modListVersion"]);

            var version = QJsonValueToInt(versionNode, -1);

            // Some mods set the number with "", so it's a String instead
            if (version < 0)
            {
                version = ParseInt(StringOr(versionNode));
            }

            if (version != 2)
            {
                // qWarning() << "The value of 'modListVersion' is ... (expected "2")! The file may be corrupted.";
            }

            var arrayVal = obj.Contains("modlist") ? obj["modlist"] : obj["modlist"].NullOr(obj["modList"]);
            if (arrayVal is JsonArray modList)
            {
                return GetInfoFromArray(modList);
            }
        }

        return new ModDetails();
    }

    private static ModDetails GetInfoFromArray(JsonArray array)
    {
        if (array.Count == 0 || array[0] is not JsonObject firstObj)
        {
            return new ModDetails();
        }

        var details = new ModDetails();
        details.ModId = StringOr(firstObj["modid"]);
        var name = StringOr(firstObj["name"]);
        // NOTE: ignore stupid example mods copies where the author didn't even bother to change the name
        if (name != "Example Mod")
        {
            details.Name = name;
        }
        details.Version = StringOr(firstObj["version"]);
        var homeurl = StringOr(firstObj["url"]).Trim();
        if (homeurl.Length != 0)
        {
            // fix up url.
            if (!homeurl.StartsWith("http://", StringComparison.Ordinal)
                && !homeurl.StartsWith("https://", StringComparison.Ordinal)
                && !homeurl.StartsWith("ftp://", StringComparison.Ordinal))
            {
                homeurl = "http://" + homeurl;
            }
        }
        details.Homeurl = homeurl;
        details.Description = StringOr(firstObj["description"]);
        var authors = firstObj["authorList"] as JsonArray;
        if (authors is not { Count: > 0 })
        {
            // FIXME: what is the format of this? is there any?
            authors = firstObj["authors"] as JsonArray;
        }

        if (firstObj.ContainsKey("logoFile"))
        {
            details.IconFile = StringOr(firstObj["logoFile"]);
        }

        if (authors is not null)
        {
            foreach (var author in authors)
            {
                details.Authors.Add(StringOr(author));
            }
        }

        if (details.ModId.StartsWith("mod_", StringComparison.Ordinal))
        {
            details.ModId = details.ModId[4..];
        }

        void AddDep(string dep)
        {
            if (dep == "mod_MinecraftForge" || dep == "Forge")
            {
                return;
            }
            if (dep.Contains(':'))
            {
                dep = dep[(dep.IndexOf(':') + 1)..];
            }
            if (dep.Contains('@'))
            {
                dep = dep[..dep.IndexOf('@')];
            }
            if (dep.StartsWith("mod_", StringComparison.Ordinal))
            {
                dep = dep[4..];
            }
            details.Dependencies.Add(dep);
        }

        if (firstObj.ContainsKey("requiredMods"))
        {
            if (firstObj["requiredMods"] is JsonArray requiredMods)
            {
                foreach (var dep in requiredMods)
                {
                    AddDep(StringOr(dep));
                }
            }
        }
        else if (firstObj.ContainsKey("dependencies"))
        {
            if (firstObj["dependencies"] is JsonArray dependencies)
            {
                foreach (var dep in dependencies)
                {
                    AddDep(StringOr(dep));
                }
            }
        }

        return details;
    }

    // https://github.com/MinecraftForge/Documentation/blob/5ab4ba6cf9abc0ac4c0abd96ad187461aefd72af/docs/gettingstarted/structuring.md
    public static ModDetails ReadMcModToml(string contents)
    {
        var details = new ModDetails();

        TomlTable tomlData;
        try
        {
            // toml++ skips a leading UTF-8 BOM, like the JSON reader above
            tomlData = MiniToml.Parse(contents.TrimStart('\uFEFF'));
        }
        catch (Exception) // upstream #6047: malformed mod metadata must not crash the scan
        {
            return new ModDetails();
        }

        // array defined by [[mods]]
        var tomlModsArr = tomlData.GetTableArray("mods");
        if (tomlModsArr is not { Count: > 0 })
        {
            // qWarning() << "Corrupted mods.toml? Couldn't find [[mods]] array!";
            return new ModDetails();
        }

        // we only really care about the first element, since multiple mods in one file is not supported by us at the moment
        var modsTable = tomlModsArr[0];

        // mandatory properties - always in [[mods]]
        details.ModId = modsTable.GetString("modId");
        details.Version = modsTable.GetString("version");
        details.Name = modsTable.GetString("displayName");
        details.Description = modsTable.GetString("description");

        // optional properties - can be in the root table or [[mods]]
        var authors = tomlData.GetString("authors");
        if (authors.Length == 0)
        {
            authors = modsTable.GetString("authors");
        }
        if (authors.Length != 0)
        {
            details.Authors.Add(authors);
        }

        var homeurl = tomlData.GetString("displayURL");
        if (homeurl.Length == 0)
        {
            homeurl = modsTable.GetString("displayURL");
        }
        // fix up url.
        if (homeurl.Length != 0 && !homeurl.StartsWith("http://", StringComparison.Ordinal)
            && !homeurl.StartsWith("https://", StringComparison.Ordinal)
            && !homeurl.StartsWith("ftp://", StringComparison.Ordinal))
        {
            homeurl = "http://" + homeurl;
        }
        details.Homeurl = homeurl;

        var issueTrackerUrl = tomlData.GetString("issueTrackerURL");
        if (issueTrackerUrl.Length == 0)
        {
            issueTrackerUrl = modsTable.GetString("issueTrackerURL");
        }
        details.IssueTracker = issueTrackerUrl;

        var license = tomlData.GetString("license");
        if (license.Length == 0)
        {
            license = modsTable.GetString("license");
        }
        if (license.Length != 0)
        {
            details.Licenses.Add(new ModLicense(license));
        }

        var logoFile = tomlData.GetString("logoFile");
        if (logoFile.Length == 0)
        {
            logoFile = modsTable.GetString("logoFile");
        }
        details.IconFile = logoFile;

        void ParseDep(IReadOnlyList<TomlTable>? dependencies)
        {
            string[] ignoreModIds = ["", "forge", "neoforge", "minecraft"];
            if (dependencies is null)
            {
                return;
            }
            foreach (var depTable in dependencies)
            {
                var modId = depTable.GetString("modId");
                if (ignoreModIds.Contains(modId))
                {
                    continue;
                }
                var isNeoForgeDep = depTable.GetString("type") == "required";
                var isForgeDep = depTable.GetBoolean("mandatory");
                if (isNeoForgeDep || isForgeDep)
                {
                    details.Dependencies.Add(modId);
                }
            }
        }

        if (tomlData.TryGetValue("dependencies", out var depValue))
        {
            if (depValue is List<object?> depArray)
            {
                ParseDep(depArray.OfType<TomlTable>().ToList());
            }
            else if (depValue is TomlTable depTable)
            {
                var expectedKey = details.ModId;
                if (!depTable.ContainsKey(expectedKey))
                {
                    var firstKey = depTable.FirstKey();
                    if (firstKey is not null)
                    {
                        expectedKey = firstKey;
                    }
                }
                ParseDep(depTable.GetTableArray(expectedKey));
            }
        }

        return details;
    }

    // https://fabricmc.net/wiki/documentation:fabric_mod_json
    public static ModDetails ReadFabricModInfo(string contents)
    {
        // QJsonDocument::fromJson on garbage yields an empty object; every lookup defaults.
        var object0 = ParseJson(contents) as JsonObject ?? [];
        var schemaVersion = object0.Contains("schemaVersion") ? QJsonValueToInt(object0["schemaVersion"], 0) : 0;

        var details = new ModDetails();

        details.ModId = StringOr(object0["id"]);
        details.Version = StringOr(object0["version"]);

        details.Name = object0.Contains("name") ? StringOr(object0["name"]) : details.ModId;
        details.Description = StringOr(object0["description"]);

        if (schemaVersion >= 1)
        {
            if (object0["authors"] is JsonArray authors)
            {
                foreach (var author in authors)
                {
                    if (author is JsonObject authorObj)
                    {
                        details.Authors.Add(StringOr(authorObj["name"]));
                    }
                    else
                    {
                        details.Authors.Add(StringOr(author));
                    }
                }
            }

            if (object0["contact"] is JsonObject contact)
            {
                if (contact.ContainsKey("homepage"))
                {
                    details.Homeurl = StringOr(contact["homepage"]);
                }
                if (contact.ContainsKey("issues"))
                {
                    details.IssueTracker = StringOr(contact["issues"]);
                }
            }

            if (object0["license"] is { } license)
            {
                switch (license)
                {
                    case JsonArray licenseArray:
                        foreach (var l in licenseArray)
                        {
                            if (l is JsonValue)
                            {
                                details.Licenses.Add(new ModLicense(StringOr(l)));
                            }
                            else if (l is JsonObject licenseObj)
                            {
                                details.Licenses.Add(new ModLicense(StringOr(licenseObj["name"]),
                                    StringOr(licenseObj["id"]), StringOr(licenseObj["url"]),
                                    StringOr(licenseObj["description"])));
                            }
                        }
                        break;
                    case JsonValue:
                        details.Licenses.Add(new ModLicense(StringOr(license)));
                        break;
                    case JsonObject licenseObj:
                        details.Licenses.Add(new ModLicense(StringOr(licenseObj["name"]), StringOr(licenseObj["id"]),
                            StringOr(licenseObj["url"]), StringOr(licenseObj["description"])));
                        break;
                }
            }

            if (object0["icon"] is { } icon)
            {
                if (icon is JsonObject iconObj)
                {
                    // take the largest icon
                    var largest = 0;
                    var bestIcon = "";
                    foreach (var (key, value) in iconObj)
                    {
                        var size = ParseInt(key.Split('x')[0]);
                        if (size > largest)
                        {
                            largest = size;
                            bestIcon = StringOr(value);
                        }
                    }
                    if (bestIcon.Length != 0)
                    {
                        details.IconFile = bestIcon;
                    }
                    else if (!iconObj.IsEmpty())
                    {
                        // take the first
                        details.IconFile = StringOr(iconObj.First().Value);
                    }
                }
                else if (icon is JsonValue)
                {
                    details.IconFile = StringOr(icon);
                }
            }

            if (object0["depends"] is JsonObject depends)
            {
                foreach (var (key, _) in depends)
                {
                    if (key != "fabricloader" && key != "minecraft" && !key.StartsWith("fabric-", StringComparison.Ordinal))
                    {
                        details.Dependencies.Add(key);
                    }
                }
            }
        }

        return details;
    }

    // https://github.com/QuiltMC/rfcs/blob/master/specification/0002-quilt.mod.json.md
    public static ModDetails ReadQuiltModInfo(string contents)
    {
        var details = new ModDetails();
        try
        {
            var object0 = ParseJsonOrThrow(contents) as JsonObject
                ?? throw new JsonParseException("quilt.mod.jsons parent does not contain 'quilt.mod.json'");
            var schemaVersion = QJsonValueToInt(object0["schema_version"], 0);

            // https://github.com/QuiltMC/rfcs/blob/be6ba280d785395fefa90a43db48e5bfc1d15eb4/specification/0002-quilt.mod.json.md
            if (schemaVersion == 1)
            {
                var modInfo = RequireJsonObject(object0, "quilt_loader", "Quilt mod info");

                details.ModId = RequireString(modInfo, "id", "Mod ID");
                details.Version = RequireString(modInfo, "version", "Mod version");

                var modMetadata = modInfo["metadata"] as JsonObject ?? [];

                details.Name = modMetadata.Contains("name") ? StringOr(modMetadata["name"]) : details.ModId;
                details.Description = StringOr(modMetadata["description"]);

                if (modMetadata["contributors"] is JsonObject modContributors)
                {
                    // We don't really care about the role of a contributor here
                    details.Authors.AddRange(modContributors.Select(entry => entry.Key));
                }

                if (modMetadata["contact"] is JsonObject modContact)
                {
                    if (modContact.ContainsKey("homepage"))
                    {
                        details.Homeurl = RequireString(modContact, "homepage", "'homepage'");
                    }
                    if (modContact.ContainsKey("issues"))
                    {
                        details.IssueTracker = RequireString(modContact, "issues", "'issues'");
                    }
                }

                if (modMetadata["license"] is { } license)
                {
                    switch (license)
                    {
                        case JsonArray licenseArray:
                            foreach (var l in licenseArray)
                            {
                                if (l is JsonValue)
                                {
                                    details.Licenses.Add(new ModLicense(StringOr(l)));
                                }
                                else if (l is JsonObject licenseObj)
                                {
                                    details.Licenses.Add(new ModLicense(StringOr(licenseObj["name"]),
                                        StringOr(licenseObj["id"]), StringOr(licenseObj["url"]),
                                        StringOr(licenseObj["description"])));
                                }
                            }
                            break;
                        case JsonValue:
                            details.Licenses.Add(new ModLicense(StringOr(license)));
                            break;
                        case JsonObject licenseObj:
                            details.Licenses.Add(new ModLicense(StringOr(licenseObj["name"]), StringOr(licenseObj["id"]),
                                StringOr(licenseObj["url"]), StringOr(licenseObj["description"])));
                            break;
                    }
                }

                if (modMetadata["icon"] is { } icon)
                {
                    if (icon is JsonObject iconObj)
                    {
                        // take the largest icon
                        var largest = 0;
                        foreach (var (key, _) in iconObj)
                        {
                            var size = ParseInt(key.Split('x')[0]);
                            if (size > largest)
                            {
                                largest = size;
                            }
                        }
                        if (largest > 0)
                        {
                            var key = largest.ToString() + "x" + largest.ToString();
                            details.IconFile = iconObj.Contains(key) ? StringOr(iconObj[key]) : "";
                        }
                        else
                        {  // parsing the sizes failed
                            // take the first
                            if (!iconObj.IsEmpty())
                            {
                                details.IconFile = StringOr(iconObj.First().Value);
                            }
                        }
                    }
                    else if (icon is JsonValue)
                    {
                        details.IconFile = StringOr(icon);
                    }
                }

                if (object0["depends"] is JsonArray depends)
                {
                    foreach (var entry in depends)
                    {
                        string modId;
                        if (entry is JsonValue)
                        {
                            modId = StringOr(entry);
                        }
                        else if (entry is JsonObject entryObj)
                        {
                            modId = StringOr(entryObj["id"]);
                            if (entryObj.Contains("optional") && entryObj["optional"] is JsonValue optional
                                && optional.TryGetValue<bool>(out var optionalValue) && optionalValue)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            continue;
                        }
                        if (modId != "minecraft" && !modId.StartsWith("quilt_", StringComparison.Ordinal))
                        {
                            details.Dependencies.Add(modId);
                        }
                    }
                }
            }
        }
        catch (JsonParseException)
        {
            // qWarning() << "Unable to parse mod info:" << e.cause();
        }
        return details;
    }

    public static ModDetails ReadForgeInfo(string contents)
    {
        var details = new ModDetails();
        // Read the data
        details.Name = "Minecraft Forge";
        details.ModId = "Forge";
        details.Homeurl = "http://www.minecraftforge.net/forum/";

        // INIFile (QSettings IniFormat) approximation: flat "key=value" properties lines
        var ini = ParseIni(contents);

        var major = ini.TryGetValue("forge.major.number", out var v) ? v : "0";
        var minor = ini.TryGetValue("forge.minor.number", out var v2) ? v2 : "0";
        var revision = ini.TryGetValue("forge.revision.number", out var v3) ? v3 : "0";
        var build = ini.TryGetValue("forge.build.number", out var v4) ? v4 : "0";

        details.Version = major + "." + minor + "." + revision + "." + build;
        return details;
    }

    public static ModDetails ReadLiteModInfo(string contents)
    {
        var details = new ModDetails();
        var object0 = ParseJson(contents) as JsonObject ?? [];
        if (object0.Contains("name"))
        {
            details.ModId = details.Name = StringOr(object0["name"]);
        }
        if (object0.Contains("version"))
        {
            details.Version = StringOr(object0["version"]);
        }
        else
        {
            details.Version = StringOr(object0["revision"]);
        }
        details.McVersion = StringOr(object0["mcversion"]);
        var author = StringOr(object0["author"]);
        if (author.Length != 0)
        {
            details.Authors.Add(author);
        }
        details.Description = StringOr(object0["description"]);
        details.Homeurl = StringOr(object0["url"]);
        return details;
    }

    // https://git.sleeping.town/unascribed/NilLoader/src/commit/d7fc87b255fc31019ff90f80d45894927fac6efc/src/main/java/nilloader/api/NilMetadata.java#L64
    public static ModDetails ReadNilModInfo(string contents, string fname)
    {
        var details = new ModDetails();

        var cssData = new Qdcss(contents);
        var name = cssData.Get("@nilmod.name");
        var desc = cssData.Get("@nilmod.description");
        var authors = cssData.Get("@nilmod.authors");

        if (name is not null)
        {
            details.Name = name;
        }
        if (desc is not null)
        {
            details.Description = desc;
        }
        if (authors is not null)
        {
            details.Authors.Add(authors);
        }
        details.Version = cssData.Get("@nilmod.version") ?? "?";

        details.ModId = fname.Replace(".nilmod.css", string.Empty, StringComparison.Ordinal);

        return details;
    }

    // ------------------------------------------------------------------ qdcss

    /// <summary>
    /// Port of libraries/qdcss (the NilLoader "quite decent CSS" properties reader): we only
    /// need to parse a couple of string values out of a css string; the keys are
    /// "&lt;selector&gt;.&lt;property&gt;" pairs (e.g. "@nilmod.name").
    /// </summary>
    internal sealed class Qdcss
    {
        private readonly Dictionary<string, List<string>> _data = new(StringComparer.Ordinal);

        public Qdcss(string s)
        {
            // not much error handling over here...
            // the original java code used indices returned by the matcher for them, but QRE does not expose those
            // https://github.com/unascribed/NilLoader/blob/trunk/src/main/java/nilloader/api/lib/qdcss/QDCSS.java
            foreach (Match ruleset in QdcssRulesetRegex.Matches(s))
            {
                var selector = ruleset.Groups[1].Value;
                var rules = ruleset.Groups[2].Value;
                foreach (Match rule in QdcssRuleRegex.Matches(rules))
                {
                    var property = rule.Groups[1].Value;
                    string value;
                    if (rule.Groups[2].Success)
                    {
                        value = rule.Groups[2].Value;
                    }
                    else if (rule.Groups[3].Success)
                    {
                        value = rule.Groups[3].Value;
                    }
                    else
                    {
                        value = rule.Groups[4].Value;
                    }
                    var key = selector + "." + property;
                    if (!_data.TryGetValue(key, out var values))
                    {
                        values = [];
                        _data[key] = values;
                    }
                    values.Add(value);
                }
            }
        }

        /// <summary>The last value for the key, or null (std::optional&lt;QString&gt;::value_or).</summary>
        public string? Get(string key)
        {
            return _data.TryGetValue(key, out var values) && values.Count != 0 ? values[^1] : null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static JsonNode? ParseJson(string contents)
    {
        // Qt's QJsonDocument::fromJson skips a leading UTF-8 BOM; mirror that
        var trimmed = contents.TrimStart('\uFEFF');
        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode ParseJsonOrThrow(string contents)
    {
        return JsonNode.Parse(contents.TrimStart('\uFEFF')) ?? throw new JsonParseException("the document is a JSON null");
    }

    private static JsonObject RequireJsonObject(JsonObject parent, string key, string what)
    {
        if (!parent.TryGetPropertyValue(key, out var node) || node is not JsonObject obj)
        {
            throw new JsonParseException(what + " is not an object");
        }
        return obj;
    }

    private static string RequireString(JsonObject parent, string key, string what)
    {
        if (!parent.TryGetPropertyValue(key, out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var result))
        {
            throw new JsonParseException(what + " is not a string");
        }
        return result;
    }

    /// <summary>QJsonValue::toString(): the string value, or "" for missing/non-string nodes.</summary>
    private static string StringOr(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
    }

    /// <summary>QJsonValue::toInt(defaultValue): numbers truncate, everything else defaults.</summary>
    private static int QJsonValueToInt(JsonNode? node, int defaultValue)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }
            if (value.TryGetValue<double>(out var d))
            {
                return (int)d;
            }
        }
        return defaultValue;
    }

    private static int ParseInt(string s)
    {
        return int.TryParse(s, out var value) ? value : 0;
    }

    /// <summary>QString::remove(sub, Qt::CaseInsensitive): remove every occurrence.</summary>
    private static string RemoveIgnoreCase(string line, string sub)
    {
        return Regex.Replace(line, Regex.Escape(sub), string.Empty, RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>A minimal "key=value" properties reader for forgeversion.properties.</summary>
    private static Dictionary<string, string> ParseIni(string contents)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in NewlineRegex.Split(contents))
        {
            var trimmed = line.Trim().TrimStart('\uFEFF'); // the BOM skip of the readers above
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed.StartsWith('['))
            {
                continue;
            }
            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            result[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }
        return result;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Small helpers for the JsonNode/TomlTable lookups of the readers.</summary>
internal static class LocalModParseExtensions
{
    /// <summary>The first node, or the fallback when the receiver is null (the
    /// "obj.contains(a) ? obj[a] : obj[b]" C++ lookup pattern).</summary>
    public static JsonNode? NullOr(this JsonNode? node, JsonNode? fallback)
    {
        return node ?? fallback;
    }

    /// <summary>QJsonObject::isEmpty().</summary>
    public static bool IsEmpty(this JsonObject obj)
    {
        return obj.Count == 0;
    }

    /// <summary>The first key of the table (toml++ table iteration), or null when empty.</summary>
    public static string? FirstKey(this TomlTable table)
    {
        foreach (var key in table.Keys())
        {
            return key;
        }
        return null;
    }
}
