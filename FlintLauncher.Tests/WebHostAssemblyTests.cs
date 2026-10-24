// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Assembly-sequence diagnostics ("logger early, every later step
// diagnosable"): a runtime-constructor crash must reach the state-root file sink — the
// rolling file provider opens its writer in its own constructor, BEFORE the runtime
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
        string? ini = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "config"));
            ini = Path.Combine(home, "config", "flint.cfg");
            File.WriteAllText(ini, "[General]\nLanguage=x\n");
            if (OperatingSystem.IsWindows())
            {
                return; // the unreadable-file sabotage is a POSIX mode trick
            }

            File.SetUnixFileMode(ini, UnixFileMode.None);

            // the unreadable chain INI makes the import throw inside the runtime
            // constructor (IniFile.LoadFile catches IOException only) — the crash must be
            // written to the file sink before the exception re-emerges
            var crash = Record.Exception(() =>
                WebHostBuilder.BuildWebApplication(["--home-dir", home]));

            Assert.NotNull(crash); // fail-fast preserved: no half runtime, no silent start
            var log = File.ReadAllText(Path.Combine(home, "state", "logs", "Flint-0.log"));
            Assert.Contains("runtime assembly failed", log);
            Assert.Contains("UnauthorizedAccessException", log);
        }
        finally
        {
            try
            {
                if (ini is not null && File.Exists(ini) && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(ini, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                Directory.Delete(home, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
