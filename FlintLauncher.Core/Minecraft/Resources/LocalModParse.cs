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
using System.Text.RegularExpressions;
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
        var mcmodInfoPath = Path.Combine(mod.Fileinfo.FullName, "mcmod.info");
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
        // the typed root converter accepts the object form and the very old bare-array form
        McModInfoDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                contents.TrimStart('\uFEFF'), ModMetadataJsonContext.Default.McModInfoDocument);
        }
        catch (JsonException)
        {
            // the former ParseJson null path: garbage leaves filename-only details
            return new ModDetails();
        }

        // a null first entry is the old "array[0] is not an object" empty-details path
        return document?.Entries is { Count: > 0 } entries
            ? InfoFromEntry(entries[0])
            : new ModDetails();
    }

    private static ModDetails InfoFromEntry(McModEntryJson firstObj)
    {
        var details = new ModDetails
        {
            ModId = firstObj.ModId
        };
        var name = firstObj.Name;
        // NOTE: ignore stupid example mods copies where the author didn't even bother to change the name
        if (name != "Example Mod")
        {
            details.Name = name;
        }

        details.Version = firstObj.Version;
        var homeurl = firstObj.Url.Trim();
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
        details.Description = firstObj.Description;
        var authors = firstObj.AuthorList is { Count: > 0 } authorList ? authorList : firstObj.Authors;
        if (firstObj.LogoFile is not null)
        {
            details.IconFile = firstObj.LogoFile;
        }

        if (authors is not null)
        {
            details.Authors.AddRange(authors);
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

        if (firstObj.RequiredMods is not null)
        {
            foreach (var dep in firstObj.RequiredMods)
            {
                AddDep(dep);
            }
        }
        else if (firstObj.Dependencies is not null)
        {
            foreach (var dep in firstObj.Dependencies)
            {
                AddDep(dep);
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
        // a token-stream-level broken document yields empty details (the former
        // QJsonDocument::fromJson garbage path); wrong-kind members degrade per member
        // through the coercing converters
        FabricModJson? wire;
        try
        {
            wire = JsonSerializer.Deserialize(
                contents.TrimStart('\uFEFF'), ModMetadataJsonContext.Default.FabricModJson);
        }
        catch (JsonException)
        {
            wire = null;
        }

        var fabric = wire ?? new FabricModJson();
        var details = new ModDetails
        {
            ModId = fabric.Id,
            Version = fabric.Version
        };

        details.Name = fabric.Name ?? details.ModId;
        details.Description = fabric.Description;

        if (fabric.SchemaVersion >= 1)
        {
            if (fabric.Authors is { } authors)
            {
                details.Authors.AddRange(authors);
            }

            if (fabric.Contact is { } contact)
            {
                details.Homeurl = contact.Homepage ?? "";
                details.IssueTracker = contact.Issues ?? "";
            }

            AddLicenses(fabric.License, details);
            AddFabricIcon(fabric.Icon, details);

            if (fabric.Depends is { } depends)
            {
                foreach (var key in depends.Keys)
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

    /// <summary>The largest-size resolution over the icon map (the fabric form: best entry
    /// by size, else the first; the bare-string form passes through).</summary>
    private static void AddFabricIcon(FabricIconJson? icon, ModDetails details)
    {
        if (icon is null)
        {
            return;
        }

        if (icon.Sizes is { Count: > 0 } sizes)
        {
            // take the largest icon
            var largest = 0;
            var bestIcon = "";
            foreach (var (key, value) in sizes)
            {
                var size = ParseInt(key.Split('x')[0]);
                if (size > largest)
                {
                    largest = size;
                    bestIcon = value;
                }
            }

            if (bestIcon.Length != 0)
            {
                details.IconFile = bestIcon;
            }
            else
            {
                // take the first
                details.IconFile = sizes.First().Value;
            }
        }
        else
        {
            details.IconFile = icon.Single ?? "";
        }
    }

    /// <summary>The shared license mapping: the string form runs the parsing constructor
    /// (URL extraction), the object form takes the members verbatim.</summary>
    private static void AddLicenses(List<FabricLicenseJson>? licenses, ModDetails details)
    {
        foreach (var license in licenses ?? [])
        {
            details.Licenses.Add(license.IsObjectForm
                ? new ModLicense(license.Name, license.Id ?? "", license.Url ?? "", license.Description ?? "")
                : new ModLicense(license.Name));
        }
    }

    // https://github.com/QuiltMC/rfcs/blob/master/specification/0002-quilt.mod.json.md
    public static ModDetails ReadQuiltModInfo(string contents)
    {
        QuiltModJson? wire;
        try
        {
            wire = JsonSerializer.Deserialize(
                contents.TrimStart('\uFEFF'), ModMetadataJsonContext.Default.QuiltModJson);
        }
        catch (JsonException)
        {
            // the former ParseJsonOrThrow → catch(JsonException) path: garbage leaves
            // filename-only details
            return new ModDetails();
        }

        var loader = wire?.QuiltLoader;
        if (wire is null || loader is null)
        {
            // the former RequireJsonObject("quilt_loader") throw, caught to empty details
            return new ModDetails();
        }

        // https://github.com/QuiltMC/rfcs/blob/be6ba280d785395fefa90a43db48e5bfc1d15eb4/specification/0002-quilt.mod.json.md
        // (the required id/version trio keeps its empty-details degradation)
        if (wire.SchemaVersion != 1 || loader.Id.Length == 0 || loader.Version.Length == 0)
        {
            return new ModDetails();
        }

        var details = new ModDetails
        {
            ModId = loader.Id,
            Version = loader.Version
        };

        var modMetadata = loader.Metadata;
        details.Name = modMetadata?.Name ?? details.ModId;
        details.Description = modMetadata?.Description ?? "";

        if (modMetadata?.Contributors is { } contributors)
        {
            // We don't really care about the role of a contributor here
            details.Authors.AddRange(contributors.Keys);
        }

        if (modMetadata?.Contact is { } contact)
        {
            details.Homeurl = contact.Homepage ?? "";
            details.IssueTracker = contact.Issues ?? "";
        }

        AddLicenses(modMetadata?.License, details);

        if (modMetadata?.Icon is { } icon)
        {
            if (icon.Sizes is { Count: > 0 } sizes)
            {
                // take the largest icon
                var largest = 0;
                foreach (var key in sizes.Keys)
                {
                    var size = ParseInt(key.Split('x')[0]);
                    if (size > largest)
                    {
                        largest = size;
                    }
                }

                if (largest > 0)
                {
                    var key = largest + "x" + largest;
                    details.IconFile = sizes.TryGetValue(key, out var value) ? value : "";
                }
                else
                {
                    // parsing the sizes failed: take the first
                    details.IconFile = sizes.First().Value;
                }
            }
            else
            {
                details.IconFile = icon.Single ?? "";
            }
        }

        foreach (var depend in wire.Depends ?? [])
        {
            if (depend.Optional == true || depend.Id is not { } modId)
            {
                continue;
            }

            if (modId != "minecraft" && !modId.StartsWith("quilt_", StringComparison.Ordinal))
            {
                details.Dependencies.Add(modId);
            }
        }

        return details;
    }

    public static ModDetails ReadForgeInfo(string contents)
    {
        var details = new ModDetails
        {
            // Read the data
            Name = "Minecraft Forge",
            ModId = "Forge",
            Homeurl = "http://www.minecraftforge.net/forum/"
        };

        // INIFile (QSettings IniFormat) approximation: flat "key=value" properties lines
        var ini = ParseIni(contents);

        var major = ini.GetValueOrDefault("forge.major.number", "0");
        var minor = ini.GetValueOrDefault("forge.minor.number", "0");
        var revision = ini.GetValueOrDefault("forge.revision.number", "0");
        var build = ini.GetValueOrDefault("forge.build.number", "0");

        details.Version = major + "." + minor + "." + revision + "." + build;
        return details;
    }

    public static ModDetails ReadLiteModInfo(string contents)
    {
        LiteModJson? wire;
        try
        {
            wire = JsonSerializer.Deserialize(
                contents.TrimStart('\uFEFF'), ModMetadataJsonContext.Default.LiteModJson);
        }
        catch (JsonException)
        {
            wire = null; // the former ParseJson null path
        }

        var litemod = wire ?? new LiteModJson();
        var details = new ModDetails();
        if (litemod.Name.Length != 0)
        {
            details.ModId = details.Name = litemod.Name;
        }

        details.Version = litemod.Version.Length != 0 ? litemod.Version : litemod.Revision;
        details.McVersion = litemod.McVersion;
        if (litemod.Author.Length != 0)
        {
            details.Authors.Add(litemod.Author);
        }

        details.Description = litemod.Description;
        details.Homeurl = litemod.Url;
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

/// <summary>Small helpers for the TomlTable lookups of the readers.</summary>
internal static class LocalModParseExtensions
{
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
