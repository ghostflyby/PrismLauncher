// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/AssetsUtils.{h,cpp} (the AssetObject/AssetsIndex data types,
// loadAssetsIndexJson and reconstructAssets) plus the AssetsIndex::getDownloadJob download
// pipeline, placed under Launch/ because only the launch pipeline (AssetUpdateTask and
// ReconstructAssets) consumes them. Note: upstream's mapToResources JSON key is
// "map_to_resources" (LaunchProfile's makeProfileVarMapping consumer reads "mapToResources"
// through the local getAssetsDir in MinecraftInstance.cs — both spellings exist upstream).
using System.Text.Json;
using FlintLauncher.Core.IO;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Launch;

/// <summary>Port of AssetObject.</summary>
public sealed class AssetObject
{
    public string Hash { get; set; } = "";

    public long Size { get; set; }

    /// <summary>Port of AssetObject::getRelPath.</summary>
    public string GetRelPath() => (Hash.Length >= 2 ? Hash[..2] : Hash) + "/" + Hash;

    /// <summary>Port of AssetObject::getLocalPath (upstream was CWD-relative "assets/objects";
    /// the assets root is injected here).</summary>
    public string GetLocalPath(string assetsRoot) =>
        FileSystem.PathCombine(assetsRoot, "objects", GetRelPath());

    /// <summary>Port of AssetObject::getDownloadAction; null when the local file is present.</summary>
    public NetRequest? GetDownloadAction(string assetsRoot, string resourceUrl)
    {
        var objectFile = new FileInfo(GetLocalPath(assetsRoot));
        if (!objectFile.Exists || objectFile.Length != Size)
        {
            var objectDl = NetRequest.MakeFile(new Uri(resourceUrl + GetRelPath()), objectFile.FullName);
            if (Hash.Length != 0)
            {
                objectDl.AddValidator(ChecksumValidator.FromHex(System.Security.Cryptography.HashAlgorithmName.SHA1, Hash));
            }
            return objectDl;
        }
        return null;
    }
}

/// <summary>Port of AssetsIndex.</summary>
public sealed class AssetsIndex
{
    public string Id { get; set; } = "";

    public SortedDictionary<string, AssetObject> Objects { get; } = new(StringComparer.Ordinal);

    public bool IsVirtual { get; set; }

    public bool MapToResources { get; set; }

    /// <summary>Port of AssetsIndex::getDownloadJob; null when everything is already present.
    /// <paramref name="assetsRoot"/> anchors the local object store (injected).</summary>
    public NetJob? GetDownloadJob(string resourceUrl, string assetsRoot)
    {
        var job = new NetJob($"Assets for {Id}");
        foreach (var @object in Objects.Values)
        {
            var dl = @object.GetDownloadAction(assetsRoot, resourceUrl);
            if (dl is not null)
            {
                job.AddNetRequest(dl);
            }
        }
        return job.Requests.Count != 0 ? job : null;
    }
}

public static class AssetsUtils
{
    /// <summary>Port of AssetsUtils::loadAssetsIndexJson.</summary>
    public static bool LoadAssetsIndexJson(string assetsId, string path, AssetsIndex index)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return false;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            index.Id = assetsId;
            var root = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? [];

            if (root.TryGetPropertyValue("virtual", out var isVirtual) && isVirtual is JsonValue virtualValue &&
                virtualValue.TryGetValue<bool>(out var virtualFlag))
            {
                index.IsVirtual = virtualFlag;
            }
            if (root.TryGetPropertyValue("map_to_resources", out var mapToResources) && mapToResources is JsonValue mapValue &&
                mapValue.TryGetValue<bool>(out var mapFlag))
            {
                index.MapToResources = mapFlag;
            }

            if (root.TryGetPropertyValue("objects", out var objects) && objects is JsonObject objectsMap)
            {
                foreach (var (key, value) in objectsMap)
                {
                    var assetObject = new AssetObject();
                    if (value is JsonObject nested)
                    {
                        foreach (var (nestedKey, nestedValue) in nested)
                        {
                            if (nestedKey == "hash" && nestedValue is JsonValue hashValue &&
                                hashValue.TryGetValue<string>(out var hash))
                            {
                                assetObject.Hash = hash;
                            }
                            else if (nestedKey == "size" && nestedValue is JsonValue number &&
                                     number.TryGetValue<long>(out var size))
                            {
                                assetObject.Size = size;
                            }
                        }
                    }
                    index.Objects[key] = assetObject;
                }
            }
            return true;
        }
    }

    /// <summary>Port of AssetsUtils::reconstructAssets: rebuild a virtual assets folder (or the
    /// resources folder) for the given assets ID. <paramref name="assetsRoot"/> anchors the
    /// asset store (injected; upstream anchored at the launcher working directory).</summary>
    public static bool ReconstructAssets(string assetsId, string resourcesFolder, string assetsRoot)
    {
        var indexDir = FileSystem.PathCombine(assetsRoot, "indexes");
        var objectDir = FileSystem.PathCombine(assetsRoot, "objects");
        var virtualDir = FileSystem.PathCombine(assetsRoot, "virtual");

        var indexPath = FileSystem.PathCombine(indexDir, assetsId + ".json");
        var virtualRoot = FileSystem.PathCombine(virtualDir, assetsId);

        if (!File.Exists(indexPath))
        {
            return false;
        }

        var index = new AssetsIndex();
        if (!LoadAssetsIndexJson(assetsId, indexPath, index))
        {
            return false;
        }

        string? targetPath = null;
        var removeLeftovers = false;
        if (index.IsVirtual)
        {
            targetPath = virtualRoot;
            removeLeftovers = true;
        }
        else if (index.MapToResources)
        {
            targetPath = resourcesFolder;
        }

        if (targetPath is null)
        {
            return true;
        }

        var presentFiles = CollectPathsFromDir(targetPath);
        foreach (var (map, assetObject) in index.Objects)
        {
            var targetPathForMap = FileSystem.PathCombine(targetPath, map);
            var tlk = assetObject.Hash.Length >= 2 ? assetObject.Hash[..2] : assetObject.Hash;
            var originalPath = FileSystem.PathCombine(objectDir, tlk, assetObject.Hash);
            if (!File.Exists(originalPath))
            {
                continue;
            }
            presentFiles.Remove(targetPathForMap);
            if (File.Exists(targetPathForMap))
            {
                continue;
            }
            var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetPathForMap));
            if (targetDir is not null)
            {
                FileSystem.EnsureFolderPathExists(targetDir);
            }
            try
            {
                File.Copy(originalPath, targetPathForMap);
            }
            catch (IOException)
            {
                return false;
            }
        }

        // TODO: Write last used time to virtualRoot/.lastused
        if (removeLeftovers)
        {
            // (upstream only logs "Would remove <file>" here)
        }
        return true;
    }

    /// <summary>Port of the collectPathsFromDir helper.</summary>
    private static HashSet<string> CollectPathsFromDir(string dirPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(dirPath))
        {
            return result;
        }
        foreach (var file in Directory.EnumerateFiles(dirPath, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                 }))
        {
            result.Add(file);
        }
        return result;
    }
}
