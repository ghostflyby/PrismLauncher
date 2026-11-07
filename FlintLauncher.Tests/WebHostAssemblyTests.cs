// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Assembly-sequence diagnostics ("logger early, every later step
// diagnosable"): a core-assembly crash must reach the state-root file sink — the
// rolling file provider opens its writer in its own constructor, BEFORE the core
// assembly, so the WebHostBuilder catch can write the failure into Flint-0.log before
// rethrowing (fail-fast preserved, but the last words are on disk).

using FlintLauncher.Web;
using Xunit;

namespace FlintLauncher.Tests;

public class WebHostAssemblyTests
{
    [Fact]
    public void Runtime_Assembly_Crash_Is_Diagnosable_In_The_File_Log()
    {
        var home = Path.Combine(Path.GetTempPath(), $"flint-crash-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "data"));

            // a FILE occupying the instances-directory anchor makes the assembly's
            // materialization fail (EnsureFolderPathExists yields null) — the crash must be
            // written to the file sink before the exception re-emerges
            File.WriteAllText(Path.Combine(home, "data", "instances"), "occupied by a file");

            var crash = Record.Exception(() =>
                WebHostBuilder.BuildWebApplication(["--home-dir", home]));

            Assert.NotNull(crash); // fail-fast preserved: no half-assembled host, no silent start
            var log = File.ReadAllText(Path.Combine(home, "state", "logs", "Flint-0.log"));
            Assert.Contains("runtime assembly failed", log);
            Assert.Contains("NullReferenceException", log);
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
