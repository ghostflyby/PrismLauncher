// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Manual verification against a real PrismLauncher data directory.
// Read-only: skips silently when the directory is absent, never writes to the data dir.
// Instances load through the typed layer stack — a Prism-format instance.cfg imports in
// memory (read-only, nothing materializes) and InstanceType/uuid still resolve.

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Web;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class RealDataSmokeTests
{
    private const string DataDir = "/Users/ghostflyby/Library/Application Support/PrismLauncher";

    private static bool DataDirExists() => Directory.Exists(Path.Combine(DataDir, "instances"));

    [Fact]
    public void Enumerate_Real_Instances_And_Read_Settings()
    {
        if (!DataDirExists())
        {
            return; // no real data dir on this machine (CI) — nothing to verify
        }

        // the user layer: a typed store over a THROWAWAY flint.json (never writes into the
        // real data dir); the real global INI is not imported here — the defaults suffice
        // for the shape assertions below
        var user = TestGlobalSettings.CreateStore();
        var environment = new InstanceEnvironment(user.Monitor,
            FlintJsonContext.Default.InstanceConfigFile, FlintJsonContext.Default.InstanceStateFile);

        var instancesDir = Path.Combine(DataDir, "instances");
        // the real data stays READ-ONLY: the metadata a load needs is mirrored into a
        // throwaway copy, and the migration pass runs there
        var scratch = Path.Combine(Path.GetTempPath(), $"flint-realsmoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var found = 0;
            var withProfile = 0;
            foreach (var dir in Directory.EnumerateDirectories(instancesDir))
            {
                var cfg = Path.Combine(dir, "instance.cfg");
                if (!File.Exists(cfg))
                {
                    continue; // instgroups.json and co.
                }

                var mirror = Path.Combine(scratch, Path.GetFileName(dir));
                Directory.CreateDirectory(mirror);
                File.Copy(cfg, Path.Combine(mirror, "instance.cfg"));
                var mmcPack = Path.Combine(dir, "mmc-pack.json");
                var patches = Path.Combine(dir, "patches");
                if (File.Exists(mmcPack))
                {
                    File.Copy(mmcPack, Path.Combine(mirror, "mmc-pack.json"));
                    if (Directory.Exists(patches))
                    {
                        Directory.CreateDirectory(Path.Combine(mirror, "patches"));
                        foreach (var patch in Directory.EnumerateFiles(patches, "*.json"))
                        {
                            File.Copy(patch, Path.Combine(mirror, "patches", Path.GetFileName(patch)));
                        }
                    }
                }

                TestLegacyConversion.ConvertOne(new AbsolutePath(mirror));

                found++;
                var instancePath = new AbsolutePath(mirror);
                var instance = new MinecraftInstance(
                    new InstanceJsonSettingsObject(instancePath, environment), instancePath);
                Assert.Equal("OneSix", instance.InstanceType());
                Assert.False(string.IsNullOrEmpty(instance.Uuid));
                Assert.True(instance.GameRoot.Value is { Length: > 0 });

                if (File.Exists(mmcPack))
                {
                    var profile = instance.GetPackProfile();
                    profile.Load();
                    Assert.True(profile.Components.Count > 0, $"{dir}: no components loaded");
                    var mcVersion = profile.GetComponentVersion("net.minecraft");
                    Assert.False(string.IsNullOrEmpty(mcVersion));
                    withProfile++;
                }
            }

            Assert.True(found >= 3, $"expected several instances, found {found}");
            Assert.True(withProfile >= 3, $"expected several with components, got {withProfile}");
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
