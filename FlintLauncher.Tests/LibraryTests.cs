// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/Library_test.cpp (testdata in TestData/Libraries).

using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using Xunit;

namespace FlintLauncher.Tests;

public class LibraryTests
{
    private readonly HttpMetaCache _cache;

    private readonly string _dataDir;

    public LibraryTests()
    {
        _cache = new HttpMetaCache();
        _cache.AddBase("libraries", Path.GetFullPath("libraries"));
        _dataDir = Path.Combine(AppContext.BaseDirectory, "TestData", "Libraries");
    }

    private Library ReadMojangJson(string path)
    {
        var problems = new ProblemContainer();
        return MojangVersionFormat.LibraryFromJson(
            problems,
            JsonNode.Parse(File.ReadAllText(path))!,
            path);
    }

    /// <summary>get absolute path to expected storage, assuming default cache prefix</summary>
    private List<string> GetStorage(string relative)
    {
        return [FileSystem.PathCombine(_cache.GetBasePath("libraries"), relative)];
    }

    private static RuntimeContext DummyContext(string system = "linux", string arch = "64", string realArch = "amd64")
    {
        var r = new RuntimeContext
        {
            JavaArchitecture = arch,
            JavaRealArchitecture = realArch,
            System = system,
        };
        return r;
    }

    private void GetApplicableFiles(
        Library test,
        RuntimeContext r,
        out List<string> jar,
        out List<string> native,
        out List<string> native32,
        out List<string> native64,
        string overridePath)
    {
        jar = [];
        native = [];
        native32 = [];
        native64 = [];
        test.GetApplicableFiles(r, jar, native, native32, native64, overridePath);
    }

    [Fact]
    public void Test_Legacy()
    {
        var r = DummyContext();
        var test = new Library("test.package:testname:testversion");
        Assert.Equal("test.package:testname", test.ArtifactPrefix());
        Assert.False(test.IsNative());

        GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
        Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion.jar"), jar);
        Assert.Empty(native);
        Assert.Empty(native32);
        Assert.Empty(native64);
    }

    [Fact]
    public void Test_Legacy_Url()
    {
        var r = DummyContext();
        var failedFiles = new List<string>();
        var test = new Library("test.package:testname:testversion");
        test.SetRepositoryUrl("file://foo/bar");
        var downloads = test.GetDownloads(r, _cache, failedFiles, "");
        Assert.Single(downloads);
        Assert.Empty(failedFiles);
        var dl = downloads[0];
        Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion.jar", dl.Url.ToString());
    }

    [Fact]
    public void Test_Legacy_Url_Local_Broken()
    {
        var r = DummyContext();
        var test = new Library("test.package:testname:testversion");
        Assert.False(test.IsNative());
        var failedFiles = new List<string>();
        test.SetHint("local");
        var downloads = test.GetDownloads(r, _cache, failedFiles, "");
        Assert.Empty(downloads);
        Assert.Equal(["testname-testversion.jar"], failedFiles);
    }

    [Fact]
    public void Test_Legacy_Url_Local_Override()
    {
        var r = DummyContext();
        var test = new Library("com.paulscode:codecwav:20101023");
        Assert.False(test.IsNative());
        var failedFiles = new List<string>();
        test.SetHint("local");
        var downloads = test.GetDownloads(r, _cache, failedFiles, _dataDir);
        Assert.Empty(downloads);
        Assert.Empty(failedFiles);

        GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, _dataDir);
        Assert.Equal(
            [FileSystem.PathCombine(_dataDir, "codecwav-20101023.jar")],
            jar);
        Assert.Empty(native);
        Assert.Empty(native32);
        Assert.Empty(native64);
    }

    [Fact]
    public void Test_Legacy_Native()
    {
        var r = DummyContext();
        var test = new Library("test.package:testname:testversion");
        test.NativeClassifiers["linux"] = "linux";
        Assert.True(test.IsNative());
        test.SetRepositoryUrl("file://foo/bar");
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
            Assert.Empty(jar);
            Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion-linux.jar"), native);
            Assert.Empty(native32);
            Assert.Empty(native64);
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, "");
            Assert.Single(dls);
            Assert.Empty(failedFiles);
            var dl = dls[0];
            Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion-linux.jar", dl.Url.ToString());
        }
    }

    [Fact]
    public void Test_Legacy_Native_Arch()
    {
        var r = DummyContext();
        var test = new Library("test.package:testname:testversion");
        test.NativeClassifiers["linux"] = "linux-${arch}";
        test.NativeClassifiers["osx"] = "osx-${arch}";
        test.NativeClassifiers["windows"] = "windows-${arch}";
        Assert.True(test.IsNative());
        test.SetRepositoryUrl("file://foo/bar");
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
            Assert.Empty(jar);
            Assert.Empty(native);
            Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion-linux-32.jar"), native32);
            Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion-linux-64.jar"), native64);
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, "");
            Assert.Equal(2, dls.Count);
            Assert.Empty(failedFiles);
            Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion-linux-32.jar", dls[0].Url.ToString());
            Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion-linux-64.jar", dls[1].Url.ToString());
        }
        r.System = "windows";
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
            Assert.Empty(jar);
            Assert.Empty(native);
            Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion-windows-32.jar"), native32);
            Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion-windows-64.jar"), native64);
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, "");
            Assert.Equal(2, dls.Count);
            Assert.Empty(failedFiles);
            Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion-windows-32.jar", dls[0].Url.ToString());
            Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion-windows-64.jar", dls[1].Url.ToString());
        }
        r.System = "osx";
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
            Assert.Empty(jar);
            Assert.Empty(native);
            Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion-osx-32.jar"), native32);
            Assert.Equal(GetStorage("test/package/testname/testversion/testname-testversion-osx-64.jar"), native64);
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, "");
            Assert.Equal(2, dls.Count);
            Assert.Empty(failedFiles);
            Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion-osx-32.jar", dls[0].Url.ToString());
            Assert.Equal("file://foo/bar/test/package/testname/testversion/testname-testversion-osx-64.jar", dls[1].Url.ToString());
        }
    }

    [Fact]
    public void Test_Legacy_Native_Arch_Local_Override()
    {
        var r = DummyContext();
        var test = new Library("test.package:testname:testversion");
        test.NativeClassifiers["linux"] = "linux-${arch}";
        test.SetHint("local");
        Assert.True(test.IsNative());
        test.SetRepositoryUrl("file://foo/bar");
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, _dataDir);
            Assert.Empty(jar);
            Assert.Empty(native);
            Assert.Equal([FileSystem.PathCombine(_dataDir, "testname-testversion-linux-32.jar")], native32);
            Assert.Equal([FileSystem.PathCombine(_dataDir, "testname-testversion-linux-64.jar")], native64);
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, _dataDir);
            Assert.Empty(dls);
            Assert.Equal([FileSystem.PathCombine(_dataDir, "testname-testversion-linux-64.jar")], failedFiles);
        }
    }

    [Fact]
    public void Test_Onenine()
    {
        var r = DummyContext("osx");
        var test = ReadMojangJson(Path.Combine(_dataDir, "lib-simple.json"));
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
            Assert.Equal(GetStorage("com/paulscode/codecwav/20101023/codecwav-20101023.jar"), jar);
            Assert.Empty(native);
            Assert.Empty(native32);
            Assert.Empty(native64);
        }
        r.System = "linux";
        {
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, "");
            Assert.Single(dls);
            Assert.Empty(failedFiles);
            Assert.Equal("https://libraries.minecraft.net/com/paulscode/codecwav/20101023/codecwav-20101023.jar", dls[0].Url.ToString());
        }
        r.System = "osx";
        test.SetHint("local");
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, _dataDir);
            Assert.Equal([FileSystem.PathCombine(_dataDir, "codecwav-20101023.jar")], jar);
            Assert.Empty(native);
            Assert.Empty(native32);
            Assert.Empty(native64);
        }
        r.System = "linux";
        {
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, _dataDir);
            Assert.Empty(dls);
            Assert.Empty(failedFiles);
        }
    }

    [Fact]
    public void Test_Onenine_Local_Override()
    {
        var r = DummyContext("osx");
        var test = ReadMojangJson(Path.Combine(_dataDir, "lib-simple.json"));
        test.SetHint("local");
        {
            GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, _dataDir);
            Assert.Equal([FileSystem.PathCombine(_dataDir, "codecwav-20101023.jar")], jar);
            Assert.Empty(native);
            Assert.Empty(native32);
            Assert.Empty(native64);
        }
        r.System = "linux";
        {
            var failedFiles = new List<string>();
            var dls = test.GetDownloads(r, _cache, failedFiles, _dataDir);
            Assert.Empty(dls);
            Assert.Empty(failedFiles);
        }
    }

    [Fact]
    public void Test_Onenine_Native()
    {
        var r = DummyContext("osx");
        var test = ReadMojangJson(Path.Combine(_dataDir, "lib-native.json"));
        GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
        Assert.Empty(jar);
        Assert.Equal(
            GetStorage("org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-osx.jar"),
            native);
        Assert.Empty(native32);
        Assert.Empty(native64);
        var failedFiles = new List<string>();
        var dls = test.GetDownloads(r, _cache, failedFiles, "");
        Assert.Single(dls);
        Assert.Empty(failedFiles);
        Assert.Equal(
            "https://libraries.minecraft.net/org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-osx.jar",
            dls[0].Url.ToString());
    }

    [Fact]
    public void Test_Onenine_Native_Arch()
    {
        var r = DummyContext("windows");
        var test = ReadMojangJson(Path.Combine(_dataDir, "lib-native-arch.json"));
        GetApplicableFiles(test, r, out var jar, out var native, out var native32, out var native64, "");
        Assert.Empty(jar);
        Assert.Empty(native);
        Assert.Equal(GetStorage("tv/twitch/twitch-platform/5.16/twitch-platform-5.16-natives-windows-32.jar"), native32);
        Assert.Equal(GetStorage("tv/twitch/twitch-platform/5.16/twitch-platform-5.16-natives-windows-64.jar"), native64);
        var failedFiles = new List<string>();
        var dls = test.GetDownloads(r, _cache, failedFiles, "");
        Assert.Equal(2, dls.Count);
        Assert.Empty(failedFiles);
        Assert.Equal(
            "https://libraries.minecraft.net/tv/twitch/twitch-platform/5.16/twitch-platform-5.16-natives-windows-32.jar",
            dls[0].Url.ToString());
        Assert.Equal(
            "https://libraries.minecraft.net/tv/twitch/twitch-platform/5.16/twitch-platform-5.16-natives-windows-64.jar",
            dls[1].Url.ToString());
    }

    /// <summary>Rule parsing read on the JsonNode model: os-qualified allow/disallow rules
    /// gate IsActive per runtime context.</summary>
    [Fact]
    public void Test_Rules_Parse_And_Activate()
    {
        var problems = new ProblemContainer();
        var test = MojangVersionFormat.LibraryFromJson(problems, JsonNode.Parse("""
        {
            "name": "test.package:testname:testversion",
            "rules": [
                {"action": "allow", "os": {"name": "linux"}}
            ]
        }
        """)!, "rules.json");

        Assert.True(test.ApplyRules);
        Assert.Single(test.Rules);
        Assert.True(test.IsActive(DummyContext()));
        Assert.False(test.IsActive(DummyContext("osx")));
    }
}
