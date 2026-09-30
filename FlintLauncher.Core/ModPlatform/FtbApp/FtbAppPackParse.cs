// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/import_ftb/PackHelpers.{h,cpp} (+ the data half of the import_ftb
// ListModel): parse a local FTB App installation. Each modpack lives in its own folder
// holding an instance.json (uuid/id/versionId/name/version/mcVersion/totalPlayTime/
// modLoader/jvmArgs); the loader falls back to the legacy .ftbapp/version.json (or
// version.json) "targets" array. The icon is either folder.jpg or .ftbapp/logo (whose
// first byte selects the image type).
//
// Deviations (headless port):
//  - QIcon becomes the FtbAppIcon data record (path/kind/image type); no decoding.
//  - getFTBRoot()/getDynamicPath() move to FtbAppPaths with an injected RootPath.
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.ModPlatform.FtbApp;

/// <summary>Port of FTBImportAPP::Modpack (data only). An invalid parse keeps Path empty
/// (the C++ returns a default-constructed Modpack whose path is empty).</summary>
public sealed class FtbAppModpack
{
    public string Path { get; set; } = "";

    // json data
    public string Uuid { get; set; } = "";
    public int Id { get; set; }
    public int VersionId { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string McVersion { get; set; } = "";
    public int TotalPlayTime { get; set; }

    /// <summary>root["jvmArgs"].toVariant().toString(): the string when jvmArgs is a JSON
    /// string, otherwise "" (a non-string QVariant stringifies to empty).</summary>
    public string JvmArgs { get; set; } = "";

    public ModLoaderType? LoaderType { get; set; }
    public string LoaderVersion { get; set; } = "";

    public FtbAppIcon Icon { get; set; } = new();
}

/// <summary>The icon data of loadFTBIcon + the folder.jpg branch (QIcon stand-in).</summary>
public sealed class FtbAppIcon
{
    public enum Kind : byte
    {
        None,
        /// <summary>folder.jpg next to the instance.</summary>
        FolderJpg,
        /// <summary>.ftbapp/logo: first byte is the image type, then the raw image data.</summary>
        Logo,
    }

    public Kind IconKind { get; set; } = Kind.None;

    /// <summary>The absolute path of the icon source (folder.jpg or .ftbapp/logo).</summary>
    public string FilePath { get; set; } = "";

    /// <summary>The image type byte of the .ftbapp/logo file ("png"/"jpg"/"gif"/"webp";
    /// empty for folder.jpg or unknown headers).</summary>
    public string ImageType { get; set; } = "";

    public bool IsValid => IconKind != Kind.None;
}

public static class FtbAppPackParse
{
    /// <summary>Map of FTB image type header byte -> image type (loadFTBIcon).</summary>
    private static readonly Dictionary<byte, string> ImageTypeMap = new()
    {
        [0x00] = "png",
        [0x01] = "jpg",
        [0x02] = "gif",
        [0x03] = "webp",
    };

    /// <summary>Port of FTBImportAPP::parseDirectory. Returns a modpack with an empty
    /// <see cref="FtbAppModpack.Path"/> when instance.json is missing or unreadable.</summary>
    public static FtbAppModpack ParseDirectory(string path)
    {
        var modpack = new FtbAppModpack { Path = path };
        var instanceFile = FileSystem.PathCombine(path, "instance.json");
        if (!File.Exists(instanceFile))
        {
            return new FtbAppModpack();
        }
        try
        {
            using var doc = JsonUtils.RequireDocumentFromFile(instanceFile, "FTB_APP instance JSON file");
            var root = RequireObject(doc.RootElement);
            modpack.Uuid = JsonUtils.RequireString(root, "uuid", "uuid");
            modpack.Id = JsonUtils.RequireInteger(root, "id", "id");
            modpack.VersionId = JsonUtils.RequireInteger(root, "versionId", "versionId");
            modpack.Name = JsonUtils.RequireString(root, "name", "name");
            modpack.Version = JsonUtils.RequireString(root, "version", "version");
            modpack.McVersion = JsonUtils.RequireString(root, "mcVersion", "mcVersion");
            modpack.JvmArgs = root.TryGetPropertyValue("jvmArgs", out var jvmArgs)
                && jvmArgs is JsonValue jvmValue
                && jvmValue.TryGetValue<string>(out var jvmString) ? jvmString : "";
            modpack.TotalPlayTime = JsonUtils.RequireInteger(root, "totalPlayTime", "totalPlayTime");

            var modLoader = JsonUtils.RequireString(root, "modLoader", "modLoader");
            if (modLoader.Length != 0)
            {
                // Qt split('-', KeepEmptyParts): "forge-" yields ["forge", ""]
                var parts = modLoader.Split('-');
                if (parts.Length >= 2)
                {
                    var loader = parts[0].ToLowerInvariant();
                    modpack.LoaderVersion = parts[1].Trim();
                    modpack.LoaderType = loader switch
                    {
                        "neoforge" => ModLoaderType.NeoForge,
                        "forge" => ModLoaderType.Forge,
                        "fabric" => ModLoaderType.Fabric,
                        "quilt" => ModLoaderType.Quilt,
                        _ => null,
                    };
                }
            }
        }
        catch (Exception e) when (e is JsonParseException or JsonException or FormatException)
        {
            return new FtbAppModpack();
        }
        if (modpack.LoaderType is null)
        {
            modpack.LoaderType = LegacyInstanceParsing(path, out var loaderVersion);
            modpack.LoaderVersion = loaderVersion;
        }

        modpack.Icon = LoadFtbIcon(path);
        return modpack;
    }

    /// <summary>Port of FTBImportAPP::legacyInstanceParsing: the loader comes from the
    /// "targets" array of .ftbapp/version.json (fallback version.json).</summary>
    public static (ModLoaderType? LoaderType, string LoaderVersion) LegacyInstanceParsing(string path)
    {
        var loaderType = LegacyInstanceParsing(path, out var loaderVersion);
        return (loaderType, loaderVersion);
    }

    private static ModLoaderType? LegacyInstanceParsing(string path, out string loaderVersion)
    {
        loaderVersion = "";
        var versionsFile = FileSystem.PathCombine(path, ".ftbapp", "version.json");
        if (!File.Exists(versionsFile))
        {
            versionsFile = FileSystem.PathCombine(path, "version.json");
        }
        if (!File.Exists(versionsFile))
        {
            return null;
        }
        try
        {
            using var doc = JsonUtils.RequireDocumentFromFile(versionsFile, "FTB_APP version JSON file");
            var root = RequireObject(doc.RootElement);
            var targets = JsonUtils.RequireArray(root, "targets", "targets");

            foreach (var target in targets.EnumerateArray())
            {
                var obj = RequireObject(JsonUtils.RequireObject(target, "target"));
                var name = JsonUtils.RequireString(obj, "name", "name");
                var version = JsonUtils.RequireString(obj, "version", "version");
                switch (name)
                {
                    case "neoforge":
                        loaderVersion = version;
                        return ModLoaderType.NeoForge;
                    case "forge":
                        loaderVersion = version;
                        return ModLoaderType.Forge;
                    case "fabric":
                        loaderVersion = version;
                        return ModLoaderType.Fabric;
                    case "quilt":
                        loaderVersion = version;
                        return ModLoaderType.Quilt;
                }
            }
        }
        catch (Exception e) when (e is JsonParseException or JsonException or FormatException)
        {
        }
        return null;
    }

    /// <summary>Port of the icon selection in parseDirectory (+ loadFTBIcon's type byte
    /// mapping): folder.jpg wins, else .ftbapp/logo with its leading image-type byte.</summary>
    public static FtbAppIcon LoadFtbIcon(string path)
    {
        var iconFile = FileSystem.PathCombine(path, "folder.jpg");
        if (File.Exists(iconFile))
        {
            return new FtbAppIcon { IconKind = FtbAppIcon.Kind.FolderJpg, FilePath = iconFile };
        }

        // the logo is a file that the first bit denotes the image tipe followed by the
        // actual image data
        var logoPath = FileSystem.PathCombine(path, ".ftbapp", "logo");
        var icon = new FtbAppIcon { IconKind = FtbAppIcon.Kind.Logo, FilePath = logoPath };
        if (!File.Exists(logoPath))
        {
            return icon;
        }
        try
        {
            using var stream = File.OpenRead(logoPath);
            var type = stream.ReadByte();
            if (type < 0)
            {
                return icon;
            }
            if (!ImageTypeMap.TryGetValue((byte)type, out var imageType))
            {
                return icon;
            }
            icon.ImageType = imageType;
        }
        catch (IOException)
        {
        }
        return icon;
    }

    /// <summary>Port of ListModel::update's scanPath: parse every subdirectory of an FTB App
    /// instances folder, keeping the valid modpacks (parseDirectory gave a path) and the
    /// first entry for each path.</summary>
    public static List<FtbAppModpack> ParseInstancesDirectory(string path)
    {
        var modpacks = new List<FtbAppModpack>();
        if (path.Length == 0 || !Directory.Exists(path))
        {
            return modpacks;
        }

        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            var modpack = ParseDirectory(dir);
            if (modpack.Path.Length != 0)
            {
                modpacks.Add(modpack);
            }
        }
        return modpacks;
    }

    private static JsonObject RequireObject(JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText()) as JsonObject
            ?? throw new JsonParseException("Value is not an object");
    }
}
