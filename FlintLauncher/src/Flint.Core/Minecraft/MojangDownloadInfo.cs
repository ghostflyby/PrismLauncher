// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/MojangDownloadInfo.h, including the hardcoded legacy/S3
// fallback asset index URLs of MojangAssetIndexInfo.
namespace Flint.Core.Minecraft;

public class MojangDownloadInfo
{
    /// <summary>Local filesystem path. WARNING: not used, only here so we can pass through mojang files unmolested! (null = unset)</summary>
    public string? Path { get; set; }

    /// <summary>absolute URL of this file</summary>
    public string Url { get; set; } = "";

    /// <summary>sha-1 checksum of the file</summary>
    public string Sha1 { get; set; } = "";

    /// <summary>size of the file in bytes</summary>
    public int Size { get; set; }
}

public class MojangLibraryDownloadInfo
{
    public MojangLibraryDownloadInfo()
    {
    }

    public MojangLibraryDownloadInfo(MojangDownloadInfo? artifact)
    {
        Artifact = artifact;
    }

    public MojangDownloadInfo? Artifact { get; set; }

    public Dictionary<string, MojangDownloadInfo> Classifiers { get; } = [];

    public MojangDownloadInfo? GetDownloadInfo(string? classifier)
    {
        if (classifier is null)
        {
            return Artifact;
        }
        return Classifiers.GetValueOrDefault(classifier);
    }
}

public class MojangAssetIndexInfo : MojangDownloadInfo
{
    public MojangAssetIndexInfo()
    {
    }

    public MojangAssetIndexInfo(string id)
    {
        Id = id;
        // HACK: ignore assets from other version files than Minecraft
        // workaround for stupid assets issue caused by amazon:
        // https://www.theregister.co.uk/2017/02/28/aws_is_awol_as_s3_goes_haywire/
        if (id == "legacy")
        {
            Url = "https://piston-meta.mojang.com/mc/assets/legacy/c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729/legacy.json";
        }
        // HACK
        else
        {
            Url = "https://s3.amazonaws.com/Minecraft.Download/indexes/" + id + ".json";
        }
        Known = false;
    }

    public int TotalSize { get; set; }

    public string Id { get; set; } = "";

    public bool Known { get; set; } = true;
}
