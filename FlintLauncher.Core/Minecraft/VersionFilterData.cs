// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/VersionFilterData.{h,cpp}: the static version tables, copied
// value-for-value from the C++ constructor. The QDateTime fields use DateTimeOffset?
// (null = invalid QDateTime); the C++ global instance g_VersionFilterData becomes the
// VersionFilterData.Instance singleton.
namespace FlintLauncher.Core.Minecraft;

public readonly record struct FmlLib(string Filename, string Checksum);

public sealed class VersionFilterData
{
    /// <summary>Port of the g_VersionFilterData global.</summary>
    public static readonly VersionFilterData Instance = new();

    private VersionFilterData()
    {
        // 1.3.*
        var libs13 = new List<FmlLib>
        {
            new("argo-2.25.jar", "bb672829fde76cb163004752b86b0484bd0a7f4b"),
            new("guava-12.0.1.jar", "b8e78b9af7bf45900e14c6f958486b6ca682195f"),
            new("asm-all-4.0.jar", "98308890597acb64047f7e896638e0d98753ae82"),
        };

        FmlLibsMapping["1.3.2"] = libs13;

        // 1.4.*
        var libs14 = new List<FmlLib>
        {
            new("argo-2.25.jar", "bb672829fde76cb163004752b86b0484bd0a7f4b"),
            new("guava-12.0.1.jar", "b8e78b9af7bf45900e14c6f958486b6ca682195f"),
            new("asm-all-4.0.jar", "98308890597acb64047f7e896638e0d98753ae82"),
            new("bcprov-jdk15on-147.jar", "b6f5d9926b0afbde9f4dbe3db88c5247be7794bb"),
        };

        FmlLibsMapping["1.4"] = libs14;
        FmlLibsMapping["1.4.1"] = libs14;
        FmlLibsMapping["1.4.2"] = libs14;
        FmlLibsMapping["1.4.3"] = libs14;
        FmlLibsMapping["1.4.4"] = libs14;
        FmlLibsMapping["1.4.5"] = libs14;
        FmlLibsMapping["1.4.6"] = libs14;
        FmlLibsMapping["1.4.7"] = libs14;

        // 1.5
        FmlLibsMapping["1.5"] =
        [
            new("argo-small-3.2.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51"),
            new("guava-14.0-rc3.jar", "931ae21fa8014c3ce686aaa621eae565fefb1a6a"),
            new("asm-all-4.1.jar", "054986e962b88d8660ae4566475658469595ef58"),
            new("bcprov-jdk15on-148.jar", "960dea7c9181ba0b17e8bab0c06a43f0a5f04e65"),
            new("deobfuscation_data_1.5.zip", "5f7c142d53776f16304c0bbe10542014abad6af8"),
            new("scala-library.jar", "458d046151ad179c85429ed7420ffb1eaf6ddf85"),
        ];

        // 1.5.1
        FmlLibsMapping["1.5.1"] =
        [
            new("argo-small-3.2.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51"),
            new("guava-14.0-rc3.jar", "931ae21fa8014c3ce686aaa621eae565fefb1a6a"),
            new("asm-all-4.1.jar", "054986e962b88d8660ae4566475658469595ef58"),
            new("bcprov-jdk15on-148.jar", "960dea7c9181ba0b17e8bab0c06a43f0a5f04e65"),
            new("deobfuscation_data_1.5.1.zip", "22e221a0d89516c1f721d6cab056a7e37471d0a6"),
            new("scala-library.jar", "458d046151ad179c85429ed7420ffb1eaf6ddf85"),
        ];

        // 1.5.2
        FmlLibsMapping["1.5.2"] =
        [
            new("argo-small-3.2.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51"),
            new("guava-14.0-rc3.jar", "931ae21fa8014c3ce686aaa621eae565fefb1a6a"),
            new("asm-all-4.1.jar", "054986e962b88d8660ae4566475658469595ef58"),
            new("bcprov-jdk15on-148.jar", "960dea7c9181ba0b17e8bab0c06a43f0a5f04e65"),
            new("deobfuscation_data_1.5.2.zip", "446e55cd986582c70fcf12cb27bc00114c5adfd9"),
            new("scala-library.jar", "458d046151ad179c85429ed7420ffb1eaf6ddf85"),
        ];

        // don't use installers for those.
        ForgeInstallerBlacklist = ["1.5.2"];

        // FIXME upstream: remove, used for deciding when core mods should display
        LegacyCutoffDate = ParseUtils.TimeFromS3Time("2013-06-25T15:08:56+02:00");
        LwjglWhitelist =
        [
            "net.java.jinput:jinput",
            "net.java.jinput:jinput-platform",
            "net.java.jutils:jutils",
            "org.lwjgl.lwjgl:lwjgl",
            "org.lwjgl.lwjgl:lwjgl_util",
            "org.lwjgl.lwjgl:lwjgl-platform",
        ];

        Java8BeginsDate = ParseUtils.TimeFromS3Time("2017-03-30T09:32:19+00:00");
        Java16BeginsDate = ParseUtils.TimeFromS3Time("2021-05-12T11:19:15+00:00");
        Java17BeginsDate = ParseUtils.TimeFromS3Time("2021-11-16T17:04:48+00:00");
    }

    /// <summary>mapping between minecraft versions and FML libraries required</summary>
    public Dictionary<string, List<FmlLib>> FmlLibsMapping { get; } = [];

    /// <summary>set of minecraft versions for which using forge installers is blacklisted</summary>
    public HashSet<string> ForgeInstallerBlacklist { get; }

    /// <summary>no new versions below this date will be accepted from Mojang servers</summary>
    public DateTimeOffset? LegacyCutoffDate { get; }

    /// <summary>Libraries that belong to LWJGL</summary>
    public HashSet<string> LwjglWhitelist { get; }

    /// <summary>release date of first version to require Java 8 (17w13a)</summary>
    public DateTimeOffset? Java8BeginsDate { get; }

    /// <summary>release data of first version to require Java 16 (21w19a)</summary>
    public DateTimeOffset? Java16BeginsDate { get; }

    /// <summary>release data of first version to require Java 17 (1.18 Pre Release 2)</summary>
    public DateTimeOffset? Java17BeginsDate { get; }
}
