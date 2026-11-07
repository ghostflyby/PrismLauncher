// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests: the Options face of the flint.json store — the monitor follows every commit and
// hands the snapshot itself, the change token is spent per commit and renewed, a batch Set
// is one commit that refuses unknown keys before writing, Reload adopts an external edit
// and stays silent for the store's own saves, a re-format, a missing or a corrupt file
// (never chasing its own evidence copy), the DI faces (IOptions / IOptionsSnapshot /
// IOptionsMonitor) hand the store's record, the watcher tenancy reloads on a pulse, and the
// host commits a PUT /settings group once and re-applies the network stack from the
// subscription — for an external edit of flint.json too.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using FlintLauncher.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class FlintConfigOptionsTests : IDisposable
{
    private readonly AbsolutePath _dir = Temporary.SystemTempDirectory() / $"flint-options-{Guid.NewGuid():N}";

    private AbsolutePath ConfigPath => _dir / "flint.json";

    public FlintConfigOptionsTests() => _dir.CreateDirectory();

    public void Dispose()
    {
        try
        {
            _dir.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    private FlintConfigStore NewStore() =>
        new(ConfigPath, FlintJsonContext.Default.FlintConfigFile);

    // ------------------------------------------------------------------ the monitor

    [Fact]
    public void Monitor_Follows_Every_Commit_And_Hands_The_Snapshot_Itself()
    {
        var store = NewStore();
        Assert.Same(store.Current, store.Monitor.CurrentValue);

        store.Set("RequestTimeout", 120);
        Assert.Equal(120, store.Monitor.CurrentValue.Launcher.RequestTimeout);
        Assert.Same(store.Current, store.Monitor.CurrentValue);

        store.Update(resolved => resolved with { Launcher = resolved.Launcher with { RequestTimeout = 30 } });
        Assert.Equal(30, store.Monitor.CurrentValue.Launcher.RequestTimeout);
        Assert.Same(store.Current, store.Monitor.CurrentValue);
    }

    [Fact]
    public void OnChange_Hears_Each_Commit_In_Order_With_The_Committed_Snapshot()
    {
        var store = NewStore();
        var seen = new List<int?>();
        using var subscription = store.Monitor.OnChange(snapshot =>
        {
            Assert.Same(store.Current, snapshot);
            seen.Add(snapshot.Launcher.RequestTimeout);
        });

        store.Set("RequestTimeout", 10);
        store.Set("RequestTimeout", 20);
        store.Reset("RequestTimeout");
        Assert.Equal(new int?[] { 10, 20, 60 }, seen);
    }

    [Fact]
    public void Named_Variants_Are_Refused()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.Monitor.Get("other"));
        Assert.Same(store.Current, store.Monitor.Get(Options.DefaultName));
    }

    // ------------------------------------------------------------- the change token

    [Fact]
    public void ChangeToken_Is_Spent_Per_Commit_And_Renewed()
    {
        var store = NewStore();
        var before = store.GetChangeToken();
        Assert.False(before.HasChanged);
        Assert.True(before.ActiveChangeCallbacks);

        store.Set("ShowConsole", true);
        Assert.True(before.HasChanged);
        Assert.False(store.GetChangeToken().HasChanged);

        // the ChangeToken.OnChange shape re-subscribes to the renewed token after each fire
        var fired = 0;
        using var registration = ChangeToken.OnChange(store.GetChangeToken, () => fired++);
        store.Set("ShowConsole", false);
        store.Set("ShowConsole", true);
        store.Set("ShowConsole", false);
        Assert.Equal(3, fired);
    }

    // ------------------------------------------------------------------ batch writes

    [Fact]
    public void Batch_Set_Is_One_Commit_And_Refuses_Unknown_Keys_Before_Writing()
    {
        var store = NewStore();
        var commits = 0;
        using var subscription = store.Monitor.OnChange(_ => commits++);

        store.Set(new Dictionary<string, object?>
        {
            ["ProxyType"] = "HTTP",
            ["ProxyAddr"] = "10.1.2.3",
            ["ProxyPort"] = 8118,
            ["ProxyUser"] = "corpus",
        });
        Assert.Equal(1, commits);
        var proxy = store.Current.Proxy;
        Assert.Equal(("HTTP", "10.1.2.3", 8118, "corpus"),
            (proxy.ProxyType, proxy.ProxyAddr, proxy.ProxyPort, proxy.ProxyUser));

        Assert.Throws<ArgumentException>(() => store.Set(new Dictionary<string, object?>
        {
            ["ProxyPort"] = 1,
            ["NotAKey"] = 2,
        }));
        Assert.Equal(1, commits);
        Assert.Equal(8118, store.Current.Proxy.ProxyPort);

        // an empty batch commits nothing
        store.Set(new Dictionary<string, object?>());
        Assert.Equal(1, commits);
    }

    // ------------------------------------------------------------------------ reload

    [Fact]
    public void Reload_Adopts_An_External_Edit_And_Notifies_Once()
    {
        var store = NewStore();
        store.Set("RequestTimeout", 60);
        var commits = 0;
        using var subscription = store.Monitor.OnChange(_ => commits++);

        // an editor rewrites a partial payload: the fold fills the rest from the defaults
        ConfigPath.WriteAllText("""{"launcher":{"requestTimeout":120},"proxy":{"proxyPort":3128}}""");
        Assert.True(store.Reload());
        Assert.Equal(120, store.Monitor.CurrentValue.Launcher.RequestTimeout);
        Assert.Equal(3128, store.Monitor.CurrentValue.Proxy.ProxyPort);
        Assert.Equal("None", store.Monitor.CurrentValue.Proxy.ProxyType);
        Assert.Equal(1, commits);

        // the same bytes again: nothing new on disk
        Assert.False(store.Reload());
        Assert.Equal(1, commits);
    }

    [Fact]
    public void Reload_Adopts_An_External_Restore_Of_The_Last_Adopted_Bytes()
    {
        var store = NewStore();
        store.Set("RequestTimeout", 60);
        const string external = """{"launcher":{"requestTimeout":120}}""";
        ConfigPath.WriteAllText(external);
        Assert.True(store.Reload());
        Assert.Equal(120, store.Current.Launcher.RequestTimeout);

        // the store commits over the adopted edit, and the watcher echo of that commit is
        // lost (coalesced away): the last bytes KNOWN to be on disk are the commit's, not
        // the adopted edit's
        store.Set("RequestTimeout", 30);

        // an external actor restores the adopted bytes: still a change against the commit
        ConfigPath.WriteAllText(external);
        Assert.True(store.Reload());
        Assert.Equal(120, store.Monitor.CurrentValue.Launcher.RequestTimeout);
    }

    [Fact]
    public void Reload_Is_Silent_For_Own_Saves_And_A_Reformat()
    {
        var store = NewStore();
        var commits = 0;
        using var subscription = store.Monitor.OnChange(_ => commits++);

        store.Set("RequestTimeout", 45);
        Assert.Equal(1, commits);
        // the watcher reports our own save too — it resolves to the current snapshot
        Assert.False(store.Reload());
        Assert.Equal(1, commits);

        // a whitespace-only rewrite of the same content is not a change either
        var pretty = JsonNode.Parse(ConfigPath.ReadAllText())!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        ConfigPath.WriteAllText(pretty);
        Assert.False(store.Reload());
        Assert.Equal(1, commits);
        Assert.Equal(45, store.Current.Launcher.RequestTimeout);
    }

    [Fact]
    public void Reload_Keeps_The_Snapshot_For_A_Missing_Or_Corrupt_File_And_Does_Not_Chase_Its_Evidence()
    {
        var store = NewStore();
        store.Set("RequestTimeout", 45);
        var commits = 0;
        using var subscription = store.Monitor.OnChange(_ => commits++);

        ConfigPath.WriteAllText("{ not json");
        Assert.False(store.Reload());
        Assert.Equal(45, store.Current.Launcher.RequestTimeout);
        Assert.Equal(0, commits);
        var evidence = new AbsolutePath(ConfigPath.Value + ".corrupt");
        Assert.True(evidence.ExistsFile());

        // the evidence copy is itself a change in the watched directory: the next raise must
        // not re-read (and re-copy) the same corrupt payload, or the watcher would loop
        evidence.Delete();
        Assert.False(store.Reload());
        Assert.False(evidence.ExistsFile());

        ConfigPath.Delete();
        Assert.False(store.Reload());
        Assert.Equal(45, store.Current.Launcher.RequestTimeout);
        Assert.Equal(0, commits);

        // the next write re-creates the file from the kept snapshot
        store.Set("ShowConsole", true);
        Assert.True(ConfigPath.ExistsFile());
        Assert.Equal(45, store.Current.Launcher.RequestTimeout);
        Assert.Equal(1, commits);
    }

    [Fact]
    public void Failed_Save_Still_Notifies_And_Throws_The_Persist_Failure()
    {
        var store = NewStore();
        store.Set("RequestTimeout", 60);
        var commits = 0;
        FlintConfigFile? heard = null;
        using var subscription = store.Monitor.OnChange(snapshot =>
        {
            commits++;
            heard = snapshot;
        });

        if (OperatingSystem.IsWindows())
        {
            // the unwritable-config-directory probe is unix modes
            return;
        }

        File.SetUnixFileMode(_dir.Value,
            UnixFileMode.UserRead | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => store.Set("RequestTimeout", 120));
        }
        finally
        {
            File.SetUnixFileMode(_dir.Value,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // readers already observe the replaced snapshot, so the change faces heard it — but
        // the persisted file still carries the last successful save
        Assert.Equal(1, commits);
        Assert.NotNull(heard);
        Assert.Equal(120, heard.Launcher.RequestTimeout);
        Assert.Equal(120, store.Current.Launcher.RequestTimeout);
        Assert.Contains("\"requestTimeout\":60", ConfigPath.ReadAllText());
    }

    // ----------------------------------------------------------------- the DI faces

    [Fact]
    public void DI_Faces_Hand_The_Stores_Record_And_Follow_Its_Changes()
    {
        var store = NewStore();
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IOptionsFactory<FlintConfigFile>>(store);
        services.AddSingleton<IOptionsChangeTokenSource<FlintConfigFile>>(store);
        services.AddSingleton(store.Monitor);
        using var provider = services.BuildServiceProvider();

        // IOptions: the snapshot at first access, frozen for the provider's lifetime (by design)
        var options = provider.GetRequiredService<IOptions<FlintConfigFile>>();
        Assert.Same(store.Current, options.Value);

        store.Set("RequestTimeout", 99);

        // IOptionsMonitor: the store's own instance, current
        var monitor = provider.GetRequiredService<IOptionsMonitor<FlintConfigFile>>();
        Assert.Same(store.Monitor, monitor);
        Assert.Equal(99, monitor.CurrentValue.Launcher.RequestTimeout);

        // IOptionsSnapshot: per scope, current at the scope's first access — a later scope
        // reads the later snapshot (the load-bearing per-scope property)
        using (var scope = provider.CreateScope())
        {
            var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<FlintConfigFile>>();
            Assert.Equal(99, snapshot.Value.Launcher.RequestTimeout);
        }

        store.Set("RequestTimeout", 100);
        using (var second = provider.CreateScope())
        {
            var snapshot = second.ServiceProvider.GetRequiredService<IOptionsSnapshot<FlintConfigFile>>();
            Assert.Equal(100, snapshot.Value.Launcher.RequestTimeout);
        }

        Assert.NotEqual(100, options.Value.Launcher.RequestTimeout);
    }

    // ------------------------------------------------------------- the watcher tenancy

    [Fact]
    public void Watcher_Tenancy_Reloads_On_Pulse_And_Ignores_Own_Saves()
    {
        var store = NewStore();
        store.Set("RequestTimeout", 60);
        using var space = new DirectorySpace();
        store.AttachWatcher(space);
        Assert.True(space.IsRegistered(_dir));
        var commits = 0;
        using var subscription = store.Monitor.OnChange(_ => commits++);

        // the own save raises the watch too: one commit, no echo
        store.Set("RequestTimeout", 61);
        space.Pulse();
        Assert.Equal(1, commits);

        ConfigPath.WriteAllText("""{"launcher":{"requestTimeout":120}}""");
        space.Pulse();
        Assert.Equal(2, commits);
        Assert.Equal(120, store.Monitor.CurrentValue.Launcher.RequestTimeout);
    }
}

// --------------------------------------------------------------------- host wiring

[Collection("LauncherJars")]
public class SettingsOptionsApiTests
{
    [Fact]
    public async Task Put_Settings_Commits_A_Group_Once_And_Applies_Through_The_Subscription()
    {
        await using var host = await FlintApiHost.StartAsync();
        var store = host.Services.GetRequiredService<FlintConfigStore>();
        var primary = host.Services.GetRequiredService<NetPrimaryHandler>();
        var commits = 0;
        using var subscription = store.Monitor.OnChange(_ => commits++);

        var put = await host.PutAsync("/api/v1/settings", new
        {
            group = "proxy",
            values = new Dictionary<string, object>
            {
                ["ProxyType"] = "HTTP",
                ["ProxyAddr"] = "10.1.2.3",
                ["ProxyPort"] = 8118,
                ["ProxyUser"] = "corpus",
                ["ProxyPass"] = "hunter2",
            },
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        // the four config keys are one commit; the subscription applied the tuple (the
        // password is the secret store's, applied by the handler's explicit pass)
        Assert.Equal(1, commits);
        Assert.Equal(("HTTP", "10.1.2.3", 8118, "corpus", "hunter2"), primary.Proxy);

        // a secret-only write replaces no snapshot: no commit, still applied
        var clear = await host.PutAsync("/api/v1/settings", new
        {
            group = "proxy",
            values = new Dictionary<string, object> { ["ProxyPass"] = "" },
        });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.Equal(1, commits);
        Assert.Equal(("HTTP", "10.1.2.3", 8118, "corpus", ""), primary.Proxy);

        // an unknown key refuses the whole group — nothing was written
        var bad = await host.PutAsync("/api/v1/settings", new
        {
            group = "proxy",
            values = new Dictionary<string, object> { ["ProxyPort"] = 1, ["NotAKey"] = 1 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(1, commits);
        Assert.Equal(8118, store.Current.Proxy.ProxyPort);
    }

    [Fact]
    public async Task External_Edit_Of_FlintJson_Reloads_And_Reapplies_The_Network_Stack()
    {
        await using var host = await FlintApiHost.StartAsync();
        var store = host.Services.GetRequiredService<FlintConfigStore>();
        var retry = host.Services.GetRequiredService<RetryHandler>();
        var space = host.Services.GetRequiredService<DirectorySpace>();

        var put = await host.PutAsync("/api/v1/settings", new
        {
            group = "launcher",
            values = new Dictionary<string, object> { ["RequestTimeout"] = 45 },
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(45), retry.RequestTimeout);

        // an editor rewrites the file behind the host's back: the tenancy reloads the user
        // layer and the subscription re-applies it — no restart, no PUT
        var configPath = Path.Combine(host.HomeDir, "config", "flint.json");
        var edited = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        edited["launcher"]!["requestTimeout"] = 120;
        await File.WriteAllTextAsync(configPath, edited.ToJsonString());
        space.Pulse();

        Assert.Equal(120, store.Monitor.CurrentValue.Launcher.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), retry.RequestTimeout);
        var settings = await host.GetAsync("/api/v1/settings");
        settings.EnsureSuccessStatusCode();
        Assert.Equal(120, JsonDocument.Parse(await settings.Content.ReadAsStringAsync()).RootElement
            .GetProperty("groups").GetProperty("launcher").GetProperty("RequestTimeout").GetInt32());
    }
}
