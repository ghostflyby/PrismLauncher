// SPDX-License-Identifier: GPL-3.0-only
// fix-3 core-level tests: the C++-aligned GlobalSettingsRegistration defaults, the
// LaunchTask.parseXmlLogs wiring (LogParser behind the XmlParsingEnabled gate), the
// Http proxy/timeout hook application (RuntimeNetConfig) and the meta-cache eviction the
// DELETE /api/v1/meta-cache endpoint delegates to.
//
// Joins "LauncherJars": Http.Configure/ManualRetries/RequestTimeout and the
// LaunchTask.XmlParsingEnabled gate are process-global statics — the tests mutate and
// restore them, so they must not race the API-host suites.
using FlintLauncher.Core;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using FlintLauncher.Web.Services;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("LauncherJars")]
public class CoverageFixCoreTests
{
    /// <summary>A completely blank settings double: nothing pre-registered, so
    /// RegisterGlobalDefaults' values are observable (same pattern as TestGlobalSettings).</summary>
    private sealed class BlankSettingsStub : SettingsObject
    {
        private readonly Dictionary<string, object?> _values = new();

        protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

        protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

        internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

        public override void SuspendSave()
        {
        }

        public override void ResumeSave()
        {
        }
    }

    // ---------------------------------------------------------------- defaults (fix-3)

    [Fact]
    public void Global_Defaults_Match_Cpp_RegisterSettings()
    {
        var settings = new BlankSettingsStub();
        GlobalSettingsRegistration.RegisterGlobalDefaults(settings);

        // console (C++: ShowConsoleOnError/ConsoleOverflowStop true)
        Assert.True(Variant.AsBool(settings.Get("ShowConsoleOnError")));
        Assert.True(Variant.AsBool(settings.Get("ConsoleOverflowStop")));
        Assert.Equal(100000, Variant.AsInt(settings.Get("ConsoleMaxLines")));
        // java (C++ default is the empty path; the automatic-java switches derive from it)
        Assert.Equal("", Variant.AsString(settings.Get("JavaPath")));
        Assert.True(Variant.AsBool(settings.Get("AutomaticJavaSwitch")));
        Assert.True(Variant.AsBool(settings.Get("AutomaticJavaDownload")));
        Assert.False(Variant.AsBool(settings.Get("UserAskedAboutAutomaticJavaDownload")));
        Assert.False(Variant.AsBool(settings.Get("IgnoreJavaWizard")));
        Assert.Equal("", Variant.AsString(settings.Get("LastHostname")));
        // memory (C++: PermGen 128, LowMemWarning true, MaxMemAlloc = defaultMaxJvmMem)
        Assert.Equal(512, Variant.AsInt(settings.Get("MinMemAlloc")));
        Assert.Equal(GlobalSettingsRegistration.DefaultMaxMemAlloc, Variant.AsInt(settings.Get("MaxMemAlloc")));
        Assert.Equal(128, Variant.AsInt(settings.Get("PermGen")));
        Assert.True(Variant.AsBool(settings.Get("LowMemWarning")));
        // network (NumberOfManualRetries/RequestTimeout/Proxy*)
        Assert.Equal(10, Variant.AsInt(settings.Get("NumberOfConcurrentTasks")));
        Assert.Equal(6, Variant.AsInt(settings.Get("NumberOfConcurrentDownloads")));
        Assert.Equal(1, Variant.AsInt(settings.Get("NumberOfManualRetries")));
        Assert.Equal(60, Variant.AsInt(settings.Get("RequestTimeout")));
        Assert.Equal("None", Variant.AsString(settings.Get("ProxyType")));
        Assert.Equal("127.0.0.1", Variant.AsString(settings.Get("ProxyAddr")));
        Assert.Equal(8080, Variant.AsInt(settings.Get("ProxyPort")));
        Assert.Equal("", Variant.AsString(settings.Get("ProxyUser")));
        Assert.Equal("", Variant.AsString(settings.Get("ProxyPass")));
        // mirrors / overrides
        Assert.Equal("", Variant.AsString(settings.Get("MetaURLOverride")));
        Assert.Equal("", Variant.AsString(settings.Get("ResourceURLOverride")));
        Assert.Equal("", Variant.AsString(settings.Get("LegacyFMLLibsURLOverride")));
        Assert.Equal("", Variant.AsString(settings.Get("MSAClientIDOverride")));
        Assert.Equal("", Variant.AsString(settings.Get("UserAgentOverride")));
        // folders
        Assert.Equal("mods", Variant.AsString(settings.Get("CentralModsDir")));
        Assert.Equal("icons", Variant.AsString(settings.Get("IconsDir")));
        Assert.Equal("skins", Variant.AsString(settings.Get("SkinsDir")));
        Assert.Equal("java", Variant.AsString(settings.Get("JavaDir")));
        Assert.False(string.IsNullOrEmpty(Variant.AsString(settings.Get("DownloadsDir"))));
        // game time / modpack flow switches
        Assert.True(Variant.AsBool(settings.Get("ShowGameTime")));
        Assert.True(Variant.AsBool(settings.Get("ShowGlobalGameTime")));
        Assert.True(Variant.AsBool(settings.Get("RecordGameTime")));
        Assert.True(Variant.AsBool(settings.Get("DownloadGameFilesDuringInstanceCreation")));
        Assert.False(Variant.AsBool(settings.Get("ModDependenciesDisabled")));
        Assert.False(Variant.AsBool(settings.Get("SkipModpackUpdatePrompt")));
        Assert.False(Variant.AsBool(settings.Get("ShowModIncompat")));
        // paste defaults (registerSettings "HACK" block: mclo.gs)
        Assert.Equal((int)PasteUpload.PasteType.Mclogs, Variant.AsInt(settings.Get("PastebinType")));
        Assert.Equal("", Variant.AsString(settings.Get("PastebinCustomAPIBase")));

        // idempotent like RegisterSetting's duplicate check
        GlobalSettingsRegistration.RegisterGlobalDefaults(settings);
        Assert.Equal(128, Variant.AsInt(settings.Get("PermGen")));
    }

    [Fact]
    public void DefaultMaxMemAlloc_Follows_The_SysInfo_Formula()
    {
        // SysInfo::defaultMaxJvmMem: total < 6 GiB -> total/1.5, else 4096; the port falls
        // back to 4096 when the RAM size is unknown
        var totalRam = HardwareInfo.TotalRamMiB();
        var expected = totalRam == 0 ? 4096 : totalRam < 6144UL ? (int)(totalRam / 1.5) : 4096;
        Assert.Equal(expected, GlobalSettingsRegistration.DefaultMaxMemAlloc);
        Assert.InRange(GlobalSettingsRegistration.DefaultMaxMemAlloc, 1, 4096);
    }

    // ------------------------------------------------------------ parseXmlLogs (fix-3)

    [Fact]
    public async Task XmlParsing_Disabled_Appends_Game_Lines_Raw()
    {
        // the gate is process-global and the Web host turns it on at startup, so the "off"
        // behavior is asserted with the gate explicitly cleared (restored below)
        var previous = LaunchTask.XmlParsingEnabled;
        LaunchTask.XmlParsingEnabled = false;
        try
        {
            using var fixture = new LaunchFixture();
            var instance = await fixture.CreateInstance();
            var task = LaunchTask.Create(instance);

            var xmlEvent = """
                <log4j:event logger="net.minecraft" timestamp="1700000000" level="INFO" thread="Server thread"><log4j:Message>raw path</log4j:Message></log4j:event>
                """;
            task.OnLogLine(xmlEvent, MessageLevel.StdOut);

            Assert.Contains(task.GetLogModel().Entries, e => e.Line == xmlEvent);
        }
        finally
        {
            LaunchTask.XmlParsingEnabled = previous;
        }
    }

    [Fact]
    public async Task XmlParsing_Enabled_Structures_Log4j_Events_And_Keeps_Plain_Lines()
    {
        var previous = LaunchTask.XmlParsingEnabled;
        LaunchTask.XmlParsingEnabled = true;
        try
        {
            using var fixture = new LaunchFixture();
            var instance = await fixture.CreateInstance();
            var task = LaunchTask.Create(instance);

            task.OnLogLine("""
                <log4j:event logger="net.minecraft.server" timestamp="1700000000" level="INFO" thread="Server thread"><log4j:Message>hello xml world</log4j:Message></log4j:event>
                """, MessageLevel.StdOut);
            task.OnLogLine("plain game line", MessageLevel.StdOut);
            task.OnLogLine("launcher side line", MessageLevel.Launcher);

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
        finally
        {
            LaunchTask.XmlParsingEnabled = previous;
        }
    }

    [Fact]
    public async Task XmlParsing_Parse_Error_Falls_Back_To_Plain_Text()
    {
        var previous = LaunchTask.XmlParsingEnabled;
        LaunchTask.XmlParsingEnabled = true;
        try
        {
            using var fixture = new LaunchFixture();
            var instance = await fixture.CreateInstance();
            var task = LaunchTask.Create(instance);

            // missing the required logger attribute -> parse error entry, line re-parsed plain
            task.OnLogLine("""
                <log4j:event timestamp="1700000000" level="INFO"><log4j:Message>no logger</log4j:Message></log4j:event>
                """, MessageLevel.StdOut);

            var entries = task.GetLogModel().Entries;
            Assert.Contains(entries, e => e.Level == MessageLevel.Error &&
                e.Line.StartsWith("[Log4j Parse Error]"));
        }
        finally
        {
            LaunchTask.XmlParsingEnabled = previous;
        }
    }

    // --------------------------------------------------- net config application (fix-3)

    [Fact]
    public void RuntimeNetConfig_Applies_Proxy_Retries_Timeout_And_Ua()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"flint-netcfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previousUa = Http.UserAgent;
        var previousRetries = Http.ManualRetries;
        var previousTimeout = Http.RequestTimeout;
        try
        {
            var runtime = new LauncherRuntime(FlintPaths.FromDataDir(dataDir));
            runtime.GlobalSettings.Set("ProxyType", "HTTP");
            runtime.GlobalSettings.Set("ProxyAddr", "10.1.2.3");
            runtime.GlobalSettings.Set("ProxyPort", 8118);
            runtime.GlobalSettings.Set("NumberOfManualRetries", 4);
            runtime.GlobalSettings.Set("RequestTimeout", 120);
            runtime.GlobalSettings.Set("UserAgentOverride", "Flint-NetCfg-Agent/9.9");

            RuntimeNetConfig.Apply(runtime);

            Assert.Equal(("HTTP", "10.1.2.3", 8118, "", ""), Http.ProxyConfig);
            Assert.Equal(4, Http.ManualRetries);
            Assert.Equal(TimeSpan.FromSeconds(120), Http.RequestTimeout);
            Assert.Equal("Flint-NetCfg-Agent/9.9", Http.UserAgent);
        }
        finally
        {
            // restore the steady state a host startup produces (the registered defaults)
            Http.Configure(FlintConfig.Default.UserAgent, "None", "127.0.0.1", 8080, "", "");
            Http.ManualRetries = previousRetries;
            Http.RequestTimeout = previousTimeout;
            Assert.Equal(("None", "127.0.0.1", 8080, "", ""), Http.ProxyConfig);
            Assert.Equal(FlintConfig.Default.UserAgent, Http.UserAgent);
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public void Http_Configure_Is_Idempotent_For_An_Unchanged_Config()
    {
        var previousUa = Http.UserAgent;
        try
        {
            Http.Configure("Flint-Idempotent/1.0", "None", "", 0, "", "");
            var client = Http.Client;
            // same configuration -> no client rebuild (connection pool survives)
            Http.Configure("Flint-Idempotent/1.0", "None", "", 0, "", "");
            Assert.Same(client, Http.Client);
            // a UA change rebuilds the client so the new header lands
            Http.Configure("Flint-Idempotent/2.0", "None", "", 0, "", "");
            Assert.NotSame(client, Http.Client);
            Assert.Equal("Flint-Idempotent/2.0", Http.UserAgent);
        }
        finally
        {
            Http.Configure(FlintConfig.Default.UserAgent, "None", "", 0, "", "");
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
                Md5Sum = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(file)))
                    .ToLowerInvariant(),
            };
            entry.SetStale(false);
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
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
