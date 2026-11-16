// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Java;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaEnvironmentTests : IDisposable
{
    private readonly List<(string name, string? value)> _savedEnv = [];

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private void SetEnv(string name, string? value)
    {
        _savedEnv.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public void Clean_DropsDangerousJavaVariables()
    {
        SetEnv("JAVA_HOME", "/opt/jdk");
        SetEnv("_JAVA_OPTIONS", "-Xmx1g");
        SetEnv("JAVA_TOOL_OPTIONS", "-agentlib:foo");
        SetEnv("FLINT_TEST_KEEP_ME", "kept");

        var environment = JavaEnvironment.Clean();

        Assert.False(environment.ContainsKey("JAVA_HOME"));
        Assert.False(environment.ContainsKey("_JAVA_OPTIONS"));
        Assert.False(environment.ContainsKey("JAVA_TOOL_OPTIONS"));
        Assert.Equal("kept", environment["FLINT_TEST_KEEP_ME"]);
    }

    [Fact]
    public void Clean_DropsLauncherPrefixedMirrors()
    {
        SetEnv("LAUNCHER_WHATEVER", "dropped");

        var environment = JavaEnvironment.Clean();

        Assert.False(environment.ContainsKey("LAUNCHER_WHATEVER"));
    }

    [Fact]
    public void RemoveEntries_RemovesAndKeepsOrder()
    {
        var separator = Path.PathSeparator;
        Assert.Equal(string.Join(separator, "a", "c"),
            JavaEnvironment.RemoveEntries(string.Join(separator, "a", "b", "c"), "b"));
        Assert.Equal(string.Join(separator, "a", "b", "c"),
            JavaEnvironment.RemoveEntries(string.Join(separator, "a", "b", "c"), "missing"));
        Assert.Equal("b",
            JavaEnvironment.RemoveEntries(string.Join(separator, "a", "b", "a"),
                string.Join(separator, "a", "a")));
    }
}
