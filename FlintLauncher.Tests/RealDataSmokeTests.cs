// SPDX-License-Identifier: GPL-3.0-only
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
        var environment = new InstanceEnvironment(user,
            FlintJsonContext.Default.InstanceConfigFile, FlintJsonContext.Default.InstanceStateFile);

        var instancesDir = Path.Combine(DataDir, "instances");
        var found = 0;
        var withProfile = 0;
        foreach (var dir in Directory.EnumerateDirectories(instancesDir))
        {
            var cfg = Path.Combine(dir, "instance.cfg");
            if (!File.Exists(cfg))
            {
                continue; // instgroups.json and co.
            }

            found++;
            var instancePath = new AbsolutePath(dir);
            var instance = new MinecraftInstance(
                new InstanceJsonSettingsObject(instancePath, environment), instancePath);
            Assert.Equal("OneSix", instance.InstanceType());
            Assert.False(string.IsNullOrEmpty(instance.Uuid));
            Assert.True(instance.GameRoot.Value is { Length: > 0 });
            // read-only: the dual-track load must not materialize anything in the real dir
            Assert.False(File.Exists(Path.Combine(dir, "instance.json")));

            var mmcPack = Path.Combine(dir, "mmc-pack.json");
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
}
