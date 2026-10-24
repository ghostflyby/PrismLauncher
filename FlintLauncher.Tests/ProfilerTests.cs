// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the profiler tools port (launcher/tools/{JProfiler,JVisualVM,GenericProfiler}): the
// registry keys from Application.cpp, the registered settings (JProfilerPath/JProfilerPort/
// JVisualVMPath with the PATH-search default), the factories' check() messages and the argv the
// profilers would spawn (the port never starts processes).

using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Tools;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class ProfilerTests
{
    /// <summary>In-memory settings double (same pattern as TestGlobalSettings).</summary>
    private sealed class TestSettings : SettingsObject
    {
        private readonly Dictionary<string, object?> _values = new();

        protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

        protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

        internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

        public override void SuspendSave() { }

        public override void ResumeSave() { }
    }

    // ---------------------------------------------------------------- registry

    private static T Factory<T>(string key) where T : ProfilerFactory
    {
        var factory = Profilers.Value(key)
                      ?? throw new Xunit.Sdk.XunitException($"the profiler registry has no '{key}' entry");
        return Assert.IsType<T>(factory);
    }

    [Fact]
    public void Registry_Has_The_Application_Keys()
    {
        // Application.cpp: m_profilers.insert("jprofiler"/"jvisualvm"/"generic", ...)
        Assert.Equal(["generic", "jprofiler", "jvisualvm"], Profilers.All.Keys.OrderBy(k => k));
        Assert.NotNull(Profilers.Value("jprofiler"));
        Assert.NotNull(Profilers.Value("jvisualvm"));
        Assert.NotNull(Profilers.Value("generic"));
        Assert.Null(Profilers.Value("missing"));
        Assert.Null(Profilers.Value(null));
    }

    [Fact]
    public void JProfiler_Registers_Path_And_Port()
    {
        var settings = new TestSettings();
        Profilers.RegisterSettings(settings);

        Assert.True(settings.Contains("JProfilerPath"));
        Assert.True(settings.Contains("JProfilerPort"));
        Assert.Equal(42042, Variant.AsInt(settings.Get("JProfilerPort")));
        // JProfilerPath registers with no default
        Assert.True(settings.Contains("JVisualVMPath"));
    }

    // ---------------------------------------------------------------- JProfiler

    [Fact]
    public void JProfiler_Builds_The_Jpenable_Command()
    {
        var settings = new TestSettings();
        var factory = Factory<JProfilerFactory>("jprofiler");
        factory.RegisterSettings(settings);
        settings.Set("JProfilerPath", "/opt/jprofiler");
        settings.Set("JProfilerPort", 51515);

        var (program, arguments) = factory.BuildCommand(settings, pid: 4321);

        // QDir(basePath).absoluteFilePath("bin/jpenable[.exe]")
        var expectedProgram = OperatingSystem.IsWindows() ? "/opt/jprofiler/bin/jpenable.exe" : "/opt/jprofiler/bin/jpenable";
        Assert.Equal(expectedProgram, program.Value);
        // { "-d", pid, "--gui", "-p", port }
        Assert.Equal(["-d", "4321", "--gui", "-p", "51515"], arguments);
    }

    [Fact]
    public void JProfiler_Default_Port_Is_42042()
    {
        var settings = new TestSettings();
        var factory = Factory<JProfilerFactory>("jprofiler");
        factory.RegisterSettings(settings);

        settings.Set("JProfilerPath", "/opt/jprofiler");
        var (program, arguments) = factory.BuildCommand(settings, pid: 7);
        var expectedName = OperatingSystem.IsWindows() ? "jpenable.exe" : "jpenable";
        Assert.EndsWith(expectedName, program.Value);
        Assert.Equal(["-d", "7", "--gui", "-p", "42042"], arguments);
    }

    [Fact]
    public void JProfiler_Check_Messages()
    {
        var factory = Factory<JProfilerFactory>("jprofiler");

        Assert.False(factory.Check((AbsolutePath?)null, out var emptyError));
        Assert.Equal("Empty path", emptyError);

        Assert.False(factory.Check(new AbsolutePath("/definitely/not/a/place"), out var missingError));
        Assert.Equal("Path does not exist", missingError);

        var dir = Directory.CreateTempSubdirectory("flint-jprofiler").FullName;
        try
        {
            Assert.False(factory.Check(new AbsolutePath(dir), out var invalidError));
            Assert.Equal("Invalid JProfiler install", invalidError);

            // bin/ + bin/jprofiler (or .exe) + bin/agent.jar make it valid
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(dir, "bin", "jprofiler.exe"), "");
            }
            else
            {
                File.WriteAllText(Path.Combine(dir, "bin", "jprofiler"), "");
            }

            File.WriteAllText(Path.Combine(dir, "bin", "agent.jar"), "");
            Assert.True(factory.Check(new AbsolutePath(dir), out var okError));
            Assert.Equal("", okError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- JVisualVM

    [Fact]
    public void JVisualVM_Builds_The_Openpid_Command()
    {
        var settings = new TestSettings();
        var factory = Factory<JVisualVmFactory>("jvisualvm");
        factory.RegisterSettings(settings);
        settings.Set("JVisualVMPath", "/usr/bin/visualvm");

        var (program, arguments) = factory.BuildCommand(settings, pid: 999);

        Assert.Equal("/usr/bin/visualvm", program.Value);
        // { "--openpid", pid }
        Assert.Equal(["--openpid", "999"], arguments);
    }

    [Fact]
    public void JVisualVM_Check_Messages()
    {
        var factory = Factory<JVisualVmFactory>("jvisualvm");

        Assert.False(factory.Check((AbsolutePath?)null, out var emptyError));
        Assert.Equal("Empty path", emptyError);

        Assert.False(factory.Check(new AbsolutePath("/definitely/not/visualvm"), out var missingError));
        Assert.Equal("Invalid path to VisualVM", missingError);

        var dir = Directory.CreateTempSubdirectory("flint-visualvm").FullName;
        try
        {
            // the name must contain "visualvm" and the file must be executable
            var tool = Path.Combine(dir, "visualvm-run");
            File.WriteAllText(tool, "");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tool,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Assert.True(factory.Check(new AbsolutePath(tool), out var okError));
            Assert.Equal("", okError);

            var wrongName = Path.Combine(dir, "somethingelse");
            File.WriteAllText(wrongName, "");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(wrongName,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Assert.False(factory.Check(new AbsolutePath(wrongName), out var nameError));
            Assert.Equal("Invalid path to VisualVM", nameError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // QStandardPaths::findExecutable: empty default when neither jvisualvm nor visualvm is found
    [Fact]
    public void JVisualVM_Registered_Default_Comes_From_The_Path_Search()
    {
        var settings = new TestSettings();
        var factory = Factory<JVisualVmFactory>("jvisualvm");
        factory.RegisterSettings(settings);

        var expected = JVisualVmFactory.FindExecutable("jvisualvm");
        if (expected is not { Value.Length: > 0 })
        {
            expected = JVisualVmFactory.FindExecutable("visualvm");
        }

        Assert.Equal(expected?.Value ?? "", Variant.AsString(settings.Get("JVisualVMPath")));
    }

    // ---------------------------------------------------------------- GenericProfiler

    [Fact]
    public void GenericProfiler_Is_Always_Valid_And_Spawns_Nothing()
    {
        var factory = Factory<GenericProfilerFactory>("generic");
        Assert.True(factory.Check(new TestSettings(), out var error));
        Assert.Equal("", error);
        Assert.True(factory.Check(new AbsolutePath(Path.GetTempPath()), out _));

        var (program, arguments) = factory.BuildCommand(new TestSettings(), pid: 12);
        Assert.Null(program.Value);
        Assert.Empty(arguments);
    }
}
