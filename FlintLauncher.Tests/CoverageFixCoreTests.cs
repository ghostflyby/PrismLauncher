// SPDX-License-Identifier: GPL-3.0-only
// fix-3 core-level tests: the C++-aligned config defaults (CONFIG C5/C6 — the record
// property defaults are the single source, read through the typed flint.json store; the D4
// batch retired the SettingsObject adapter view), the
// LaunchTask.parseXmlLogs wiring (LogParser behind the LaunchServices.XmlLogParsing gate),
// the Http proxy/timeout hook application (RuntimeNetConfig) and the meta-cache eviction the
// DELETE /api/v1/meta-cache endpoint delegates to.
//
// Joins "LauncherJars": NetStack.Default.Configure/ManualRetries/RequestTimeout are process-global
// statics — the tests mutate and restore them, so they must not race the API-host suites.

using System.Security.Cryptography;
using FlintLauncher.Core;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using FlintLauncher.Web;
using FlintLauncher.Web.Services;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("LauncherJars")]
public class CoverageFixCoreTests
{
    /// <summary>A fresh typed store over a temp flint.json: the resolved fold IS the record
    /// defaults, so the store's Get() observations are exactly the single-source defaults
    /// (the same key names the C++ registered).</summary>
    private static FlintConfigStore NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new FlintConfigStore(Path.Combine(dir, "flint.json"),
            FlintJsonContext.Default.FlintConfigFile);
    }

    // ---------------------------------------------------------------- defaults (fix-3)

    [Fact]
    public void Global_Defaults_Match_Cpp_RegisterSettings()
    {
        var store = NewStore();

        // console (C++: ShowConsoleOnError/ConsoleOverflowStop true)
        Assert.True(Variant.AsBool(store.Get("ShowConsoleOnError")));
        Assert.True(Variant.AsBool(store.Get("ConsoleOverflowStop")));
        Assert.Equal(100000, Variant.AsInt(store.Get("ConsoleMaxLines")));
        // java (C++ default is the empty path; the automatic-java switches derive from it)
        Assert.Equal("", Variant.AsString(store.Get("JavaPath")));
        Assert.True(Variant.AsBool(store.Get("AutomaticJavaSwitch")));
        Assert.True(Variant.AsBool(store.Get("AutomaticJavaDownload")));
        Assert.False(Variant.AsBool(store.Get("IgnoreJavaWizard")));
        // CONFIG C9: the runtime-state keys (UserAskedAboutAutomaticJavaDownload,
        // LastHostname, the java probe quintet, LastOfflinePlayerName, TotalPlayTime*) are
        // NOT settings anymore — launcher-state.json owns them (see LauncherStateStoreTests)
        Assert.Null(FlintConfigKeys.Resolve("UserAskedAboutAutomaticJavaDownload"));
        Assert.Null(FlintConfigKeys.Resolve("LastHostname"));
        Assert.Null(FlintConfigKeys.Resolve("JavaSignature"));
        Assert.Null(FlintConfigKeys.Resolve("TotalPlayTime"));
        Assert.Null(FlintConfigKeys.Resolve("LastOfflinePlayerName"));
        // C4: the secret trio is not a config member — the wire serves it from the store
        Assert.True(FlintConfigKeys.IsSecret("ProxyPass"));
        Assert.True(FlintConfigKeys.IsSecret("ModrinthToken"));
        Assert.True(FlintConfigKeys.IsSecret("FlameKeyOverride"));
        // memory (LowMemWarning true, MaxMemAlloc = defaultMaxJvmMem; PermGen died with
        // the Java <8 support floor)
        Assert.Equal(512, Variant.AsInt(store.Get("MinMemAlloc")));
        Assert.Equal(MemoryConfig.DefaultMaxMemAlloc, Variant.AsInt(store.Get("MaxMemAlloc")));
        Assert.Null(FlintConfigKeys.Resolve("PermGen"));
        Assert.True(Variant.AsBool(store.Get("LowMemWarning")));
        // network (NumberOfManualRetries/RequestTimeout/Proxy*)
        Assert.Equal(10, Variant.AsInt(store.Get("NumberOfConcurrentTasks")));
        Assert.Equal(6, Variant.AsInt(store.Get("NumberOfConcurrentDownloads")));
        Assert.Equal(1, Variant.AsInt(store.Get("NumberOfManualRetries")));
        Assert.Equal(60, Variant.AsInt(store.Get("RequestTimeout")));
        Assert.Equal("None", Variant.AsString(store.Get("ProxyType")));
        Assert.Equal("127.0.0.1", Variant.AsString(store.Get("ProxyAddr")));
        Assert.Equal(8080, Variant.AsInt(store.Get("ProxyPort")));
        Assert.Equal("", Variant.AsString(store.Get("ProxyUser")));
        // mirrors / overrides
        Assert.Equal("", Variant.AsString(store.Get("MetaURLOverride")));
        Assert.Equal("", Variant.AsString(store.Get("ResourceURLOverride")));
        Assert.Equal("", Variant.AsString(store.Get("LegacyFMLLibsURLOverride")));
        Assert.Equal("", Variant.AsString(store.Get("MSAClientIDOverride")));
        Assert.Equal("", Variant.AsString(store.Get("UserAgentOverride")));
        // folders (DATA D8: CentralModsDir is unregistered — the lookup resolves to null)
        Assert.Null(FlintConfigKeys.Resolve("CentralModsDir"));
        Assert.Equal("icons", Variant.AsString(store.Get("IconsDir")));
        Assert.Equal("skins", Variant.AsString(store.Get("SkinsDir")));
        Assert.Equal("java", Variant.AsString(store.Get("JavaDir")));
        Assert.False(string.IsNullOrEmpty(Variant.AsString(store.Get("DownloadsDir"))));
        // game time / modpack flow switches
        Assert.True(Variant.AsBool(store.Get("ShowGameTime")));
        Assert.True(Variant.AsBool(store.Get("ShowGlobalGameTime")));
        Assert.True(Variant.AsBool(store.Get("RecordGameTime")));
        Assert.True(Variant.AsBool(store.Get("DownloadGameFilesDuringInstanceCreation")));
        Assert.False(Variant.AsBool(store.Get("ModDependenciesDisabled")));
        Assert.False(Variant.AsBool(store.Get("SkipModpackUpdatePrompt")));
        Assert.False(Variant.AsBool(store.Get("ShowModIncompat")));
        // paste defaults (registerSettings "HACK" block: mclo.gs)
        Assert.Equal((int)PasteUpload.PasteType.Mclogs, Variant.AsInt(store.Get("PastebinType")));
        Assert.Equal("", Variant.AsString(store.Get("PastebinCustomAPIBase")));

        // every registry key resolves (FlintConfigKeys) and the store round-trips a write
        // into flint.json (the authoritative global source, C5)
        store.Set("LowMemWarning", false);
        Assert.False(Variant.AsBool(store.Get("LowMemWarning")));
        Assert.True(File.Exists(store.FilePath));
        var reloaded = new FlintConfigStore(store.FilePath, FlintJsonContext.Default.FlintConfigFile);
        Assert.NotNull(reloaded.Current.Memory.LowMemWarning);
        Assert.False(reloaded.Current.Memory.LowMemWarning.Value);
    }

    [Fact]
    public void DefaultMaxMemAlloc_Follows_The_SysInfo_Formula()
    {
        // SysInfo::defaultMaxJvmMem: total < 6 GiB -> total/1.5, else 4096; the port falls
        // back to 4096 when the RAM size is unknown
        var totalRam = HardwareInfo.TotalRamMiB();
        var expected = totalRam == 0 ? 4096 : totalRam < 6144UL ? (int)(totalRam / 1.5) : 4096;
        Assert.Equal(expected, MemoryConfig.DefaultMaxMemAlloc);
        Assert.InRange(MemoryConfig.DefaultMaxMemAlloc, 1, 4096);
    }

    // ------------------------------------------------------------ parseXmlLogs (fix-3)

    [Fact]
    public async Task XmlParsing_Disabled_Appends_Game_Lines_Raw()
    {
        // the gate is per-pipeline now (LaunchServices.XmlLogParsing — the Web host turns it
        // on in the services it builds), so the "off" behavior is the default fixture task
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices());

        var xmlEvent = """
                       <log4j:event logger="net.minecraft" timestamp="1700000000" level="INFO" thread="Server thread"><log4j:Message>raw path</log4j:Message></log4j:event>
                       """;
        task.OnLogLine(xmlEvent, MessageLevel.StdOut);

        Assert.Contains(task.GetLogModel().Entries, e => e.Line == xmlEvent);
    }

    [Fact]
    public async Task XmlParsing_Enabled_Structures_Log4j_Events_And_Keeps_Plain_Lines()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices(xmlLogParsing: true));

        task.OnLogLine("""
                       <log4j:event logger="net.minecraft.server" timestamp="1700000000" level="INFO" thread="Server thread"><log4j:Message>hello xml world</log4j:Message></log4j:event>
                       """, MessageLevel.StdOut);
        task.OnLogLine("plain game line", MessageLevel.StdOut);
        task.OnLogLine("launcher side line");

        var entries = task.GetLogModel().Entries;
        // the structured entry: [HH:mm:ss] [thread/LEVEL] [logger]: message, level from the event
        Assert.Contains(entries, e => e.Level == MessageLevel.Info &&
                                      e.Line.Contains("[Server thread/INFO] [net.minecraft.server]: hello xml world"));
        // plain lines stay separate entries (each line parses on its own)
        Assert.Contains(entries, e => e.Line == "plain game line");
        Assert.Contains(entries, e => e.Line == "launcher side line");
        // the raw XML did not leak into the model
        Assert.DoesNotContain(entries, e => e.Line.Contains("<log4j:event"));
    }

    [Fact]
    public async Task XmlParsing_Parse_Error_Falls_Back_To_Plain_Text()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var task = LaunchTask.Create(instance, fixture.CreateServices(xmlLogParsing: true));

        // missing the required logger attribute -> parse error entry, line re-parsed plain
        task.OnLogLine("""
                       <log4j:event timestamp="1700000000" level="INFO"><log4j:Message>no logger</log4j:Message></log4j:event>
                       """, MessageLevel.StdOut);

        var entries = task.GetLogModel().Entries;
        Assert.Contains(entries, e => e.Level == MessageLevel.Error &&
                                      e.Line.StartsWith("[Log4j Parse Error]"));
    }

    // --------------------------------------------------- net config application (fix-3)

    [Fact]
    public void RuntimeNetConfig_Applies_Proxy_Retries_Timeout_And_Ua()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"flint-netcfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previousRetries = NetStack.Default.ManualRetries;
        var previousTimeout = NetStack.Default.RequestTimeout;
        try
        {
            var runtime = new LauncherRuntime(FlintPaths.FromHomeDir(dataDir));
            runtime.Config.Set("ProxyType", "HTTP");
            runtime.Config.Set("ProxyAddr", "10.1.2.3");
            runtime.Config.Set("ProxyPort", 8118);
            runtime.Config.Set("NumberOfManualRetries", 4);
            runtime.Config.Set("RequestTimeout", 120);
            runtime.Config.Set("UserAgentOverride", "Flint-NetCfg-Agent/9.9");

            RuntimeNetConfig.Apply(runtime);

            Assert.Equal(("HTTP", "10.1.2.3", 8118, "", ""), NetStack.Default.ProxyConfig);
            Assert.Equal(4, NetStack.Default.ManualRetries);
            Assert.Equal(TimeSpan.FromSeconds(120), NetStack.Default.RequestTimeout);
            Assert.Equal("Flint-NetCfg-Agent/9.9", NetStack.Default.UserAgent);
        }
        finally
        {
            // restore the steady state a host startup produces (the registered defaults)
            NetStack.Default.Configure(FlintConfig.Default.UserAgent, "None", "127.0.0.1", 8080, "", "");
            NetStack.Default.ManualRetries = previousRetries;
            NetStack.Default.RequestTimeout = previousTimeout;
            Assert.Equal(("None", "127.0.0.1", 8080, "", ""), NetStack.Default.ProxyConfig);
            Assert.Equal(FlintConfig.Default.UserAgent, NetStack.Default.UserAgent);
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void Http_Configure_Is_Idempotent_For_An_Unchanged_Config()
    {
        try
        {
            NetStack.Default.Configure("Flint-Idempotent/1.0", "None", "", 0, "", "");
            var client = NetStack.Default.Client;
            // same configuration -> no client rebuild (connection pool survives)
            NetStack.Default.Configure("Flint-Idempotent/1.0", "None", "", 0, "", "");
            Assert.Same(client, NetStack.Default.Client);
            // a UA change rebuilds the client so the new header lands
            NetStack.Default.Configure("Flint-Idempotent/2.0", "None", "", 0, "", "");
            Assert.NotSame(client, NetStack.Default.Client);
            Assert.Equal("Flint-Idempotent/2.0", NetStack.Default.UserAgent);
        }
        finally
        {
            NetStack.Default.Configure(FlintConfig.Default.UserAgent, "None", "", 0, "", "");
        }
    }

    // ------------------------------------------------------- meta cache eviction (fix-4)

    [Fact]
    public void MetaCache_EvictAll_Drops_Entries_And_Cached_Files()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-evict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var cache = new HttpMetaCache(Path.Combine(dir, "metacache.json"));
            cache.AddBase("general", Path.Combine(dir, "cache"));
            Directory.CreateDirectory(Path.Combine(dir, "cache"));
            var file = Path.Combine(dir, "cache", "thing.bin");
            File.WriteAllBytes(file, [1, 2, 3]);

            var entry = new MetaEntry
            {
                BaseId = "general",
                RelativePath = "thing.bin",
                Md5Sum = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(file)))
                    .ToLowerInvariant(),
            };
            entry.IsStale = false;
            Assert.True(cache.UpdateEntry(entry));
            Assert.NotNull(cache.GetEntry("general", "thing.bin"));

            Assert.True(cache.EvictAll());
            Assert.Null(cache.GetEntry("general", "thing.bin"));
            Assert.False(File.Exists(file));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
