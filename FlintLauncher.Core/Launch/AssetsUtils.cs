// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/AssetsUtils.{h,cpp} (the AssetObject/AssetsIndex data types,
// loadAssetsIndexJson and reconstructAssets) plus the AssetsIndex::getDownloadJob download
// pipeline, placed under Launch/ because only the launch pipeline (AssetUpdateTask and
// ReconstructAssets) consumes them. Note: upstream's mapToResources JSON key is
// "map_to_resources" (LaunchProfile's makeProfileVarMapping consumer reads "mapToResources"
// through the local getAssetsDir in MinecraftInstance.cs — both spellings exist upstream).

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using TruePath;

namespace FlintLauncher.Core.Launch;

/// <summary>The on-disk assets index shape (assets/indexes/&lt;id&gt;.json): Mojang's
/// virtual/map_to_resources flags and the object map keyed by resource path.</summary>
public sealed record AssetsIndexFileJson(
    bool? Virtual,
    [property: JsonPropertyName("map_to_resources")]
    bool? MapToResources,
    Dictionary<string, AssetObjectJson>? Objects);

public sealed record AssetObjectJson(
    string? Hash,
    long? Size);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AssetsIndexFileJson))]
internal sealed partial class AssetsJsonContext : JsonSerializerContext;

/// <summary>Port of AssetObject.</summary>
public sealed class AssetObject
{
    public string Hash { get; init; } = "";

    public long Size { get; init; }

    /// <summary>Port of AssetObject::getRelPath.</summary>
    public string GetRelPath() => (Hash.Length >= 2 ? Hash[..2] : Hash) + "/" + Hash;

    /// <summary>Port of AssetObject::getAbsolutePath (upstream was CWD-relative "assets/objects";
    /// the assets root is injected here).</summary>
    public string GetAbsolutePath(string assetsRoot) =>
        Path.Combine(assetsRoot, "objects", GetRelPath());

    /// <summary>Port of AssetObject::getDownloadAction; null when the local file is present.</summary>
    public NetRequest? GetDownloadAction(string assetsRoot, string resourceUrl)
    {
        var objectFile = new FileInfo(GetAbsolutePath(assetsRoot));
        if (!objectFile.Exists || objectFile.Length != Size)
        {
            var objectDl = NetRequest.MakeFile(new Uri(resourceUrl + GetRelPath()), objectFile.FullName);
            if (Hash.Length != 0)
            {
                objectDl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.SHA1, Hash));
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
        AssetsIndexFileJson? document;
        try
        {
            document = JsonSerializer.Deserialize(File.ReadAllText(path),
                AssetsJsonContext.Default.AssetsIndexFileJson);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return false;
        }

        if (document is null)
        {
            return false;
        }

        index.Id = assetsId;

        if (document.Virtual is { } virtualFlag)
        {
            index.IsVirtual = virtualFlag;
        }

        if (document.MapToResources is { } mapFlag)
        {
            index.MapToResources = mapFlag;
        }

        if (document.Objects is { } objects)
        {
            foreach (var (key, value) in objects)
            {
                // a JSON null object entry kept its (empty) AssetObject in the former read
                index.Objects[key] = new AssetObject
                {
                    Hash = value?.Hash ?? "",
                    Size = value?.Size ?? 0,
                };
            }
        }

        return true;
    }

    /// <summary>Port of AssetsUtils::reconstructAssets: rebuild a virtual assets folder (or the
    /// resources folder) for the given assets ID. <paramref name="assetsRoot"/> anchors the
    /// asset store (injected; upstream anchored at the launcher working directory).</summary>
    public static bool ReconstructAssets(string assetsId, string resourcesFolder, string assetsRoot)
    {
        var indexDir = new AbsolutePath(assetsRoot) / "indexes";
        var objectDir = new AbsolutePath(assetsRoot) / "objects";
        var virtualDir = new AbsolutePath(assetsRoot) / "virtual";
        var indexPath = (indexDir / (assetsId + ".json")).Value;
        var virtualRoot = (virtualDir / assetsId).Value;

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
            var targetPathForMap = Path.Combine(targetPath, map);
            var tlk = assetObject.Hash.Length >= 2 ? assetObject.Hash[..2] : assetObject.Hash;
            var originalPath = (objectDir / tlk / assetObject.Hash).Value;
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
                FileOps.EnsureFolderPathExists(new AbsolutePath(targetDir));
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

        var files = Directory.EnumerateFiles(dirPath, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        });
        foreach (var file in files)
        {
            result.Add(file);
        }

        return result;
    }
}
