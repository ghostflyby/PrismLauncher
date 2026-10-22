// SPDX-License-Identifier: GPL-3.0-only
// Pins for the STATE S2 red lines: the default resolution path writes NOTHING to disk (the
// token is process-lifetime, generated in-process), a leftover pre-reform
// <dataRoot>/api-token file is deleted on startup and NEVER read, and the host-injection /
// disable switches behave as decided. The injection path itself is exercised end-to-end by
// the API integration tests (ApiIntegrationTests --api-token); these are the unit-level
// guards that catch a regression back to file-backed tokens.

using FlintLauncher.Web.Services;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class ApiAuthServiceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"flint-authtest-{Guid.NewGuid():N}");
    private readonly (string Name, string? Value)[] _savedEnv;

    public ApiAuthServiceTests()
    {
        // the resolver reads process env; pin the two S2 variables for the duration
        // (restored in Dispose) so an ambient environment cannot flip the default path
        _savedEnv = [PinEnv("FLINT_API_TOKEN"), PinEnv("FLINT_NO_AUTH")];
        Environment.SetEnvironmentVariable("FLINT_API_TOKEN", null);
        Environment.SetEnvironmentVariable("FLINT_NO_AUTH", null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private static (string, string?) PinEnv(string name) =>
        (name, Environment.GetEnvironmentVariable(name));

    private string DataRoot => Path.Combine(_home, "data");

    [Fact]
    public void Default_Resolution_Generates_A_Token_And_Writes_Nothing()
    {
        Directory.CreateDirectory(DataRoot);

        var auth = ApiAuthService.Resolve([]);

        Assert.True(auth.Enabled);
        Assert.True(auth.TokenGenerated);
        Assert.NotEmpty(auth.Token);
        // the S2 red line: not one file appears under the data root — the pre-reform
        // api-token file has no successor (TokenGenerated=true is the only carrier)
        Assert.Empty(Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Leftover_Token_File_Is_Never_Read_And_Is_Removed_By_The_Cleanup()
    {
        Directory.CreateDirectory(DataRoot);
        var leftover = Path.Combine(DataRoot, ApiAuthService.LegacyTokenFileName);
        File.WriteAllText(leftover, "stale-token-from-a-previous-install");

        // no compatibility read: the default path generates its own token
        var auth = ApiAuthService.Resolve([]);
        Assert.NotEqual("stale-token-from-a-previous-install", auth.Token);
        Assert.True(auth.TokenGenerated);

        // the startup cleanup (WebHostBuilder) removes the dead weight, idempotently
        ApiAuthService.RemoveLegacyTokenFile(new AbsolutePath(DataRoot));
        Assert.False(File.Exists(leftover));
        ApiAuthService.RemoveLegacyTokenFile(new AbsolutePath(DataRoot));
    }

    [Fact]
    public void Host_Injection_Provides_The_Token_And_Generates_Nothing()
    {
        var auth = ApiAuthService.Resolve(["--api-token", "host-token"]);

        Assert.True(auth.Enabled);
        Assert.Equal("host-token", auth.Token);
        Assert.False(auth.TokenGenerated);
    }

    [Fact]
    public void No_Auth_Disables_The_Service()
    {
        var auth = ApiAuthService.Resolve(["--no-auth"]);

        Assert.False(auth.Enabled);
        Assert.False(auth.TokenGenerated);
    }
}
