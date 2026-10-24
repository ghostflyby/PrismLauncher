// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/VersionFilterData.{h,cpp}: the static version tables, copied
// value-for-value from the C++ constructor. Members the port never reads (the forge
// installer blacklist, the legacy/java cutoff dates, the fml-lib checksum column) are not
// ported; the C++ global g_VersionFilterData becomes the VersionFilterData.Instance singleton.

namespace FlintLauncher.Core.Minecraft;

public readonly record struct FmlLib(string Filename);

public sealed class VersionFilterData
{
    /// <summary>Port of the g_VersionFilterData global.</summary>
    public static readonly VersionFilterData Instance = new();

    private VersionFilterData()
    {
        // 1.3.*
        var libs13 = new List<FmlLib>
        {
            new("argo-2.25.jar"),
            new("guava-12.0.1.jar"),
            new("asm-all-4.0.jar"),
        };

        FmlLibsMapping["1.3.2"] = libs13;

        // 1.4.*
        var libs14 = new List<FmlLib>
        {
            new("argo-2.25.jar"),
            new("guava-12.0.1.jar"),
            new("asm-all-4.0.jar"),
            new("bcprov-jdk15on-147.jar"),
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
            new("argo-small-3.2.jar"),
            new("guava-14.0-rc3.jar"),
            new("asm-all-4.1.jar"),
            new("bcprov-jdk15on-148.jar"),
            new("deobfuscation_data_1.5.zip"),
            new("scala-library.jar"),
        ];

        // 1.5.1
        FmlLibsMapping["1.5.1"] =
        [
            new("argo-small-3.2.jar"),
            new("guava-14.0-rc3.jar"),
            new("asm-all-4.1.jar"),
            new("bcprov-jdk15on-148.jar"),
            new("deobfuscation_data_1.5.1.zip"),
            new("scala-library.jar"),
        ];

        // 1.5.2
        FmlLibsMapping["1.5.2"] =
        [
            new("argo-small-3.2.jar"),
            new("guava-14.0-rc3.jar"),
            new("asm-all-4.1.jar"),
            new("bcprov-jdk15on-148.jar"),
            new("deobfuscation_data_1.5.2.zip"),
            new("scala-library.jar"),
        ];

        // LWJGL-owned libraries (the core-mods display filter)
        LwjglWhitelist =
        [
            "net.java.jinput:jinput",
            "net.java.jinput:jinput-platform",
            "net.java.jutils:jutils",
            "org.lwjgl.lwjgl:lwjgl",
            "org.lwjgl.lwjgl:lwjgl_util",
            "org.lwjgl.lwjgl:lwjgl-platform",
        ];
    }

    /// <summary>mapping between minecraft versions and FML libraries required</summary>
    public Dictionary<string, List<FmlLib>> FmlLibsMapping { get; } = [];

    /// <summary>Libraries that belong to LWJGL</summary>
    public HashSet<string> LwjglWhitelist { get; }
}
