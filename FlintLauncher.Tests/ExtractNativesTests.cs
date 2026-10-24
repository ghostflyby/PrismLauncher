// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the jar/asset artifact steps: ExtractNatives (synthetic native jar -> natives
// folder, with the .jnilib -> .dylib Java 8+ rename), ModMinecraftJar::createModdedJar
// (jar mod merging), ReconstructAssets (virtual assets folder rebuild) and AssetUpdateTask
// (asset index + object downloads against a local HTTP stub).
//
// AssetUpdateTask stamps its instance.Paths at AppContext.BaseDirectory, so the store-root
// "assets" dir lands on the <BaseDirectory>/assets symlink, which redirects it to a temp
// directory (a global cwd swap would race the other test classes); all such tests live in
// this one class, which xUnit keeps sequential. ReconstructAssets anchors the store at the
// fixture's injected Paths root instead (the CWD-relative "assets/" fallback is gone).

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class ExtractNativesTests
{
    internal const string NativeLibrariesPatch = """
                                                 {
                                                     "formatVersion": 1,
                                                     "name": "Minecraft",
                                                     "uid": "net.minecraft",
                                                     "version": "1.20.1",
                                                     "mainClass": "net.minecraft.client.main.Main",
                                                     "type": "release",
                                                     "assets": "5",
                                                     "minecraftArguments": "--username ${auth_player_name}",
                                                     "libraries": [
                                                         {
                                                             "name": "com.flint:testnatives:1.0",
                                                             "MMC-hint": "local",
                                                             "natives": {
                                                                 "osx-": "natives-osx", "osx": "natives-osx", "osx-arm64": "natives-osx", "osx-x86_64": "natives-osx",
                                                                 "linux": "natives-linux", "linux-arm64": "natives-linux", "linux-x86_64": "natives-linux",
                                                                 "windows": "natives-windows", "windows-x86_64": "natives-windows"
                                                             }
                                                         }
                                                     ]
                                                 }
                                                 """;

    /// <summary>Keeps the launch "running" so the steps' FinalizeStep can be observed while
    /// the extracted/merged artifacts still exist.</summary>
    private sealed class GateStep(LaunchTask parent) : LaunchStep(parent)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Started = true;
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            EmitSucceeded();
        }

        public override bool CanAbort() => true;
    }

    private static void WriteNativeJars(AbsolutePath localLibraryPath, string dylib, string jnilib)
    {
        // one synthetic jar per OS classifier; the runtime context picks the current one
        foreach (var classifier in new[] { "osx", "linux", "windows" })
        {
            var jarPath = localLibraryPath / $"testnatives-1.0-natives-{classifier}.jar";
            var jarDir = Assert.NotNull(jarPath.Parent);
            jarDir.CreateDirectory();
            using var zip = new ZipArchive(jarPath.Create(), ZipArchiveMode.Create);
            zip.CreateEntryFromBytes(dylib, "liblwjgl.dylib");
            zip.CreateEntryFromBytes(jnilib, "libtest.jnilib");
            zip.CreateEntryFromBytes("meta", "META-INF/MANIFEST.MF");
        }
    }

    [Fact]
    public async Task ExtractsNativeJars_WithJniLibRename()
    {
        using var fixture = new LaunchFixture();
        fixture.SeedJavaDetection("java", 0, languageVersion: 17, version: "17.0.2"); // >= 8: the .jnilib hack applies (state cache)
        var instance = await fixture.CreateInstance(NativeLibrariesPatch);
        WriteNativeJars(instance.GetLocalLibraryPath(), "dylib-bytes", "jnilib-bytes");

        var task = LaunchTask.Create(instance, fixture.CreateServices());
        var gate = new GateStep(task);
        task.AppendStep(new ExtractNatives(task));
        task.AppendStep(gate);

        var run = task.StartAsync();
        while (!gate.Started)
        {
            await Task.Delay(10);
        }

        // the natives are extracted at this point (ExtractNatives succeeded before the gate)
        var natives = instance.GetNativePath();
        Assert.True(natives.ExistsDirectory());
        Assert.Equal("dylib-bytes", await (natives / "liblwjgl.dylib").ReadAllTextAsync());
        // the .jnilib -> .dylib rename of the Java 8+ hack
        Assert.Equal("jnilib-bytes", await (natives / "libtest.dylib").ReadAllTextAsync());
        Assert.False((natives / "libtest.jnilib").ExistsFile());
        Assert.Equal("meta", await (natives / "META-INF/MANIFEST.MF").ReadAllTextAsync());

        gate.Release.TrySetResult();
        await run;

        Assert.Equal(TaskState.Succeeded, task.State);
        // ExtractNatives::finalize removes the whole natives folder again
        Assert.False(natives.ExistsDirectory());
    }

    [Fact]
    public async Task Skips_WhenNoNativeJars()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance(); // default patch has no libraries
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new ExtractNatives(task));

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.False(instance.GetNativePath().ExistsDirectory());
    }

    // ------------------------------------------------------------------ ModMinecraftJar

    private const string JarModPatch = """
                                       {
                                           "formatVersion": 1,
                                           "name": "Minecraft",
                                           "uid": "net.minecraft",
                                           "version": "1.20.1",
                                           "mainClass": "net.minecraft.client.main.Main",
                                           "type": "release",
                                           "assets": "5",
                                           "minecraftArguments": "--username ${auth_player_name}",
                                           "mainJar": { "name": "com.flint:minecraft:1.20.1:client", "MMC-hint": "local", "MMC-filename": "client.jar" },
                                           "+jarMods": [ { "name": "jarmod.zip" } ]
                                       }
                                       """;

    private static void WriteJarModInputs(MinecraftInstance instance)
    {
        // the "minecraft client jar" the mods get merged into
        var clientJar = instance.GetLocalLibraryPath() / "client.jar";
        var clientDir = Assert.NotNull(clientJar.Parent);
        clientDir.CreateDirectory();
        using (var zip = new ZipArchive(clientJar.Create(), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromBytes("source-common", "common.txt");
            zip.CreateEntryFromBytes("source-only", "src/only.txt");
            zip.CreateEntryFromBytes("manifest", "META-INF/MANIFEST.MF");
        }

        // the jar mod: one new file plus one entry that overrides the source's
        var jarMod = instance.JarModsDir / "jarmod.zip";
        var jarModDir = Assert.NotNull(jarMod.Parent);
        jarModDir.CreateDirectory();
        using (var zip = new ZipArchive(jarMod.Create(), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromBytes("mod-new", "aaa/new.txt");
            zip.CreateEntryFromBytes("mod-common", "common.txt");
        }
    }

    [Fact]
    public async Task ModMinecraftJar_MergesJarMods_AndCleansUpAfterLaunch()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance(JarModPatch);
        WriteJarModInputs(instance);
        Assert.NotEmpty(instance.GetJarMods()); // the patch yields one jar mod

        var task = LaunchTask.Create(instance, fixture.CreateServices());
        var gate = new GateStep(task);
        task.AppendStep(new ModMinecraftJar(task));
        task.AppendStep(gate);

        var run = task.StartAsync();
        while (!gate.Started)
        {
            await Task.Delay(10);
        }

        // the modded jar exists while the launch runs
        var finalJar = instance.BinRoot() / "minecraft.jar";
        Assert.True(finalJar.ExistsFile());
        using (var zip = new ZipArchive(finalJar.OpenRead(), ZipArchiveMode.Read))
        {
            Assert.Equal("mod-new", await ReadEntry(zip, "aaa/new.txt"));
            // the jar mod's entry wins over the source jar's (mods merge first)
            Assert.Equal("mod-common", await ReadEntry(zip, "common.txt"));
            Assert.Equal("source-only", await ReadEntry(zip, "src/only.txt"));
            // the source's META-INF is stripped when jar mods are present
            Assert.Null(zip.GetEntry("META-INF/MANIFEST.MF"));
        }

        gate.Release.TrySetResult();
        await run;

        Assert.Equal(TaskState.Succeeded, task.State);
        // ModMinecraftJar::finalize removes the temporary stripped jar
        Assert.False(finalJar.ExistsFile());
    }

    [Fact]
    public async Task ModMinecraftJar_Succeeds_WithoutJarMods()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance(); // no jar mods in the default patch
        var task = LaunchTask.Create(instance, fixture.CreateServices());
        task.AppendStep(new ModMinecraftJar(task));

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.False(instance.BinRoot().ExistsDirectory());
    }

    private static async Task<string> ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(await entry.OpenAsync());
        return await reader.ReadToEndAsync();
    }

    // ------------------------------------------------------- assets (store-root anchored)

    [Fact]
    public async Task ReconstructAssets_RebuildsTheVirtualFolder()
    {
        const string assetsId = "testassets";
        var body = Encoding.UTF8.GetBytes("virtual asset payload");
        var hash = Convert.ToHexString(SHA1.HashData(body)).ToLowerInvariant();
        using var fixture = new LaunchFixture();
        var services = fixture.CreateServices();
        // the step anchors the asset store at the injected Paths root: seed the index (which
        // marks the assets virtual) and the object file there
        var assetsRoot = services.Paths.AssetsRoot;
        var indexJson = """{"virtual":true,"objects":{"icon.png":{"hash":"HASH","size":LEN}}}"""
            .Replace("HASH", hash).Replace("LEN", body.Length.ToString());
        services.Paths.AssetIndexesDir.CreateDirectory();
        await (services.Paths.AssetIndexesDir / (assetsId + ".json")).WriteAllTextAsync(indexJson);
        var objectDir = assetsRoot / "objects" / hash[..2];
        objectDir.CreateDirectory();
        await (objectDir / hash).WriteAllBytesAsync(body);

        var patch = LaunchFixture.DefaultPatchJson.Replace("\"assets\": \"5\"", $"\"assets\": \"{assetsId}\"");
        var instance = await fixture.CreateInstance(patch);

        var task = LaunchTask.Create(instance, services);
        task.AppendStep(new ReconstructAssets(task));

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
        var rebuilt = assetsRoot / "virtual" / assetsId / "icon.png";
        Assert.True(rebuilt.ExistsFile());
        Assert.Equal("virtual asset payload", await rebuilt.ReadAllTextAsync());
    }

    [Fact]
    public async Task AssetUpdateTask_DownloadsIndexAndObjects()
    {
        const string assetsId = "testindex";
        var objectBody = Encoding.UTF8.GetBytes("asset object bytes");
        var objectHash = Convert.ToHexString(SHA1.HashData(objectBody)).ToLowerInvariant();
        var indexJson = """{"objects":{"misc/pane.png":{"hash":"HASH","size":LEN}}}"""
            .Replace("HASH", objectHash).Replace("LEN", objectBody.Length.ToString());
        var indexBody = Encoding.UTF8.GetBytes(indexJson);
        var indexSha = Convert.ToHexString(SHA1.HashData(indexBody)).ToLowerInvariant();

        var previousCwd = Environment.CurrentDirectory;
        // the redirect symlink lives at BaseDirectory/assets and the task reads "assets/..."
        // CWD-relative — pin CWD back there so foreign chdir (API hosts) cannot break it
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        using var assetsRoot = new AssetsRootRedirect();
        using var server = new TestHttpServer(context =>
        {
            var requestUrl = context.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
            var path = requestUrl.AbsolutePath.TrimEnd('/');
            byte[] payload;
            if (path.EndsWith("/indexes/" + assetsId + ".json"))
            {
                payload = indexBody;
            }
            else if (path.EndsWith("/objects/" + objectHash[..2] + "/" + objectHash))
            {
                payload = objectBody;
            }
            else
            {
                context.Response.StatusCode = 404;
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = payload.Length;
            context.Response.OutputStream.Write(payload);
        });

        using var fixture = new LaunchFixture();
        // the asset index download goes through the profile's assetIndex entry (URL + sha1),
        // the object downloads through the launcher's resource URL override
        var patch = LaunchFixture.DefaultPatchJson.Replace("\"assets\": \"5\"",
            $"\"assets\": \"{assetsId}\", \"assetIndex\": " +
            "{ \"id\": \"ID\", \"url\": \"URL/indexes/ID.json\", \"sha1\": \"SHA\", \"size\": 0, \"totalSize\": 0 }"
                .Replace("ID", assetsId).Replace("URL", server.Url).Replace("SHA", indexSha));
        var instance = await fixture.CreateInstance(patch);

        // the index is downloaded into the same place the task reads it from — the injected
        // instance cache ("asset_indexes" base) instead of the former static hook; the
        // instance stamp reads its assets anchors through the <BaseDirectory>/assets symlink
        // (the CWD-lexical fallback of FILE-LAYOUT §1.5), so the STORE root is BaseDirectory
        // (assets hang off the store root)
        var instanceCache = new HttpMetaCache(Temporary.SystemTempDirectory() / $"flint-asset-indexes-{Guid.NewGuid():N}" / "metacache.json");
        instanceCache.AddBase("asset_indexes", new AbsolutePath(Path.Combine(assetsRoot.Root, "indexes")));
        instance.MetaCache = instanceCache;
        // the layout arrives through the injected LaunchServices (the instance derives its
        // Paths from it): the instance stamp reads its assets anchors through the
        // <BaseDirectory>/assets symlink (the CWD-lexical fallback of FILE-LAYOUT §1.5), so
        // the store root is BaseDirectory (assets hang off the store root)
        var baseDir = new AbsolutePath(AppContext.BaseDirectory);
        // the resource URL override rides the instance-stamped LaunchServices (the former
        // AssetUpdateTask.ResourceUrlOverride static hook)
        instance.LaunchServices = new LaunchServices
        {
            Paths = new()
            {
                ConfigRoot = baseDir,
                DataRoot = baseDir,
                StoreRoot = baseDir,
                CacheRoot = baseDir,
                StateRoot = baseDir,
            },
            LoggerFactory = NullLoggerFactory.Instance,
            ResourceUrlOverride = server.Url + "/objects/",
        };
        try
        {
            var task = LaunchTask.Create(instance, instance.LaunchServices);
            // the update tasks run as plain FlintTasks inside the TaskStepWrapper (which
            // gates on the progressReportingRequest/proceed handshake)
            task.RequestProgress += _ => task.Proceed();
            task.AppendStep(new TaskStepWrapper(task, new AssetUpdateTask(instance)));

            await task.StartAsync();

            Assert.True(task.WasSuccessful, task.FailReason + " | indexFile=" +
                                            File.Exists(Path.Combine(assetsRoot.Root, "indexes", assetsId + ".json")) +
                                            " objectFile=" + File.Exists(Path.Combine(assetsRoot.Root, "objects", objectHash[..2], objectHash)));
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
        }

        // the index landed in assets/indexes, the object in assets/objects/<2-char>/<hash>
        Assert.Equal(indexJson, await File.ReadAllTextAsync(Path.Combine(assetsRoot.Root, "indexes", assetsId + ".json")));
        var objectPath = Path.Combine(assetsRoot.Root, "objects", objectHash[..2], objectHash);
        Assert.True(File.Exists(objectPath));
        Assert.Equal("asset object bytes", await File.ReadAllTextAsync(objectPath));
    }
}

/// <summary> MMCZip test helper: add an entry from an in-memory payload.</summary>
public static class ZipArchiveTestExtensions
{
    public static void CreateEntryFromBytes(this ZipArchive zip, string content, string name)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}

/// <summary>
/// Redirects the store-root "assets" directory for tests that stamp their Paths at
/// AppContext.BaseDirectory: the &lt;BaseDirectory&gt;/assets location becomes a directory
/// symlink into a temp dir. Falls back to a real directory when symlinks are unavailable.
/// </summary>
internal sealed class AssetsRootRedirect : IDisposable
{
    private readonly string _linkPath = Path.Combine(AppContext.BaseDirectory, "assets");
    private readonly string _target;
    private readonly bool _isLink;

    public AssetsRootRedirect()
    {
        _target = _linkPath; // the fallback root is the link path itself
        var root = Path.Combine(Path.GetTempPath(), $"flint-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            if (File.Exists(_linkPath))
            {
                File.Delete(_linkPath); // leftover symlink from a previous run
            }

            if (Directory.Exists(_linkPath))
            {
                Directory.Delete(_linkPath, recursive: true);
            }

            Directory.CreateSymbolicLink(_linkPath, root);
            _target = root;
            _isLink = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no symlink support: materialize the root in place instead
            Directory.CreateDirectory(_linkPath);
        }
    }

    /// <summary>The real root behind the "assets" symlink (the fallback root is the link path itself).</summary>
    public string Root => _target;

    public void Dispose()
    {
        try
        {
            if (_isLink)
            {
                if (Directory.Exists(_target))
                {
                    Directory.Delete(_target, recursive: true);
                }

                // remove the (possibly dangling) link itself; recursive delete would follow it
                if (Directory.Exists(_linkPath) || File.Exists(_linkPath))
                {
                    Directory.Delete(_linkPath, recursive: false);
                }
            }
            else if (Directory.Exists(_linkPath))
            {
                Directory.Delete(_linkPath, recursive: true);
            }
        }
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException or IOException
                                      or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Free-port HTTP stub (the NetStackTests pattern).</summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener;

    public TestHttpServer(Action<HttpListenerContext> handler)
    {
        // TestPorts owns the port lease AND the rebind-retry (HttpListener cannot adopt the
        // lease socket, so losing the takeover window retries on a fresh port)
        (_listener, Url) = PortAllocator.StartHttpListener();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    try
                    {
                        handler(context);
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        });
    }

    public string Url { get; }

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException) { }
    }
}
