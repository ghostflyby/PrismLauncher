// SPDX-License-Identifier: GPL-3.0-only
// A3 regression at the API surface: the migration endpoints refuse an UNKNOWN source
// directory (400 invalid_source, NO decision record — the old "prism" fallback key let a
// ~/prism-backup style import/decline seal the canonical PrismLauncher source's key away),
// and refuse to import into a target that already carries its own launcher config (the C++
// existing-data protection, Application.cpp:2029/2055 "Migration was skipped, due to
// existing data" — post-reform keyed on the config root's flint.json; the dry-run preview
// stays open above the protection).

using System.Net;
using System.Text.Json;
using Xunit;

namespace FlintLauncher.Tests;

public class MigrationApiTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"flint-mig-api-{Guid.NewGuid():N}");

    /// <summary>A synthetic Prism-family source with importable content, named like a real
    /// launcher directory under <paramref name="root"/>. Carries a whitelisted DIRECTORY that
    /// Flint does not pre-create at startup (mods/ — unlike instances/icons, which the host
    /// assembly creates and the import therefore reports as DestinationExists per its
    /// idempotent top-level skip semantics) plus the config file.</summary>
    private static string MakePrismSource(string root, string dirName = "PrismLauncher",
        string configFile = "prismlauncher.cfg")
    {
        var source = Path.Combine(root, dirName);
        Directory.CreateDirectory(Path.Combine(source, "instances", "one"));
        File.WriteAllText(Path.Combine(source, "instances", "one", "instance.cfg"), "InstanceType=OneSix\n");
        Directory.CreateDirectory(Path.Combine(source, "mods", "a"));
        File.WriteAllText(Path.Combine(source, "mods", "a", "mod.jar"), "jar");
        File.WriteAllText(Path.Combine(source, configFile), "k=v");
        return source;
    }

    /// <summary>The S3 decision record for the CANONICAL Prism source key (the one the
    /// detector files the PrismLauncher directory under) — the record an unrelated
    /// directory's import/decline must never create.</summary>
    private static string PrismRecordPath(FlintApiHost host) =>
        Path.Combine(host.HomeDir, "state", "migration", "prism.json");

    private static string ErrorCode(HttpResponseMessage response)
    {
        var json = JsonDocument.Parse(response.Content.ReadAsStream()).RootElement;
        return json.GetProperty("error").GetProperty("code").GetString()!;
    }

    // ------------------------------------------------------------- unknown-source guard

    // the finding's exact scenario: declining ~/prism-backup must not write the "prism"
    // record — that key IS the canonical PrismLauncher source's decision record
    [Fact]
    public async Task Decline_Unknown_Source_Is_Rejected_And_Writes_No_Record()
    {
        await using var host = await FlintApiHost.StartAsync();
        var backup = Path.Combine(TempRoot(), "prism-backup");
        Directory.CreateDirectory(backup);

        var response = await host.PostAsync("/api/v1/migration/prism/decline", new { source = backup });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_source", ErrorCode(response));
        Assert.False(File.Exists(PrismRecordPath(host)));
    }

    // the guard matches on the KNOWN directory names — a real Prism directory still declines
    [Fact]
    public async Task Decline_Known_Source_Still_Writes_The_Record()
    {
        await using var host = await FlintApiHost.StartAsync();
        var source = MakePrismSource(TempRoot());

        var response = await host.PostAsync("/api/v1/migration/prism/decline", new { source });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(PrismRecordPath(host)));
        Assert.Equal("prism", record.RootElement.GetProperty("source").GetString());
        Assert.Equal("declined", record.RootElement.GetProperty("decision").GetString());
    }

    // the decline record write is best-effort (like the C++ setDoNotMigrate,
    // Application.cpp:2015-2026): a state root that cannot take the record must still
    // return 204 — the worst case is the source being re-proposed, the honest undecided
    // semantic — never a 500
    [Fact]
    public async Task Decline_With_Unwritable_State_Root_Still_Returns_NoContent()
    {
        await using var host = await FlintApiHost.StartAsync();
        // a FILE occupies the migration records directory, so the endpoint's store Write throws
        var migrationDir = Path.Combine(host.HomeDir, "state", "migration");
        if (Directory.Exists(migrationDir))
        {
            Directory.Delete(migrationDir, recursive: true);
        }
        await File.WriteAllTextAsync(migrationDir, "a file blocks the migration records directory");
        var source = MakePrismSource(TempRoot());

        var response = await host.PostAsync("/api/v1/migration/prism/decline", new { source });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Import_Unknown_Source_Is_Rejected_Including_Its_DryRun()
    {
        await using var host = await FlintApiHost.StartAsync();
        var backup = Path.Combine(TempRoot(), "prism-backup");
        // even WITH whitelisted content, the directory name is not a known launcher
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "prismlauncher.cfg"), "k=v");

        foreach (var dryRun in new[] { true, false })
        {
            var response = await host.PostAsync("/api/v1/migration/prism",
                new { source = backup, mode = "copy", dryRun });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_source", ErrorCode(response));
        }
        Assert.False(File.Exists(PrismRecordPath(host)));
    }

    // ------------------------------------------------- the existing-data protection (C++)

    [Fact]
    public async Task Import_Into_Target_With_Own_Config_Is_Conflict()
    {
        await using var host = await FlintApiHost.StartAsync();
        var source = MakePrismSource(TempRoot());
        Directory.CreateDirectory(Path.Combine(host.HomeDir, "config"));
        await File.WriteAllTextAsync(Path.Combine(host.HomeDir, "config", "flint.json"), "{}");

        var response = await host.PostAsync("/api/v1/migration/prism",
            new { source, mode = "copy", dryRun = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("target_has_own_config", ErrorCode(response));
        // nothing moved, nothing recorded
        Assert.False(File.Exists(Path.Combine(host.DataDir, "prismlauncher.cfg")));
        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "mods")));
        Assert.False(File.Exists(PrismRecordPath(host)));
    }

    // the dry-run PREVIEW stays open above the protection — it is read-only planning
    [Fact]
    public async Task DryRun_Preview_Still_Plans_Above_The_Protection()
    {
        await using var host = await FlintApiHost.StartAsync();
        var source = MakePrismSource(TempRoot());
        Directory.CreateDirectory(Path.Combine(host.HomeDir, "config"));
        await File.WriteAllTextAsync(Path.Combine(host.HomeDir, "config", "flint.json"), "{}");

        var response = await host.PostAsync("/api/v1/migration/prism",
            new { source, mode = "copy", dryRun = true });

        response.EnsureSuccessStatusCode();
        var plan = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(plan.GetProperty("dryRun").GetBoolean());
        Assert.True(plan.GetProperty("totalFiles").GetInt64() > 0);
    }

    // the guard must not over-reject: a FRESH target still imports to completion, and the
    // decision record lands under the actual source name (S7)
    [Fact]
    public async Task Import_Into_Fresh_Target_Still_Runs()
    {
        await using var host = await FlintApiHost.StartAsync();
        var source = MakePrismSource(TempRoot());

        var response = await host.PostAsync("/api/v1/migration/prism",
            new { source, mode = "copy", dryRun = false });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var taskId = accepted.GetProperty("taskId").GetString()!;
        await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(30));
        // the import ran to completion: whitelisted files and un-shadowed directories landed
        // at the data root, and the decision record sealed under the ACTUAL source name (S7)
        Assert.True(File.Exists(Path.Combine(host.DataDir, "prismlauncher.cfg")));
        Assert.True(File.Exists(Path.Combine(host.DataDir, "mods", "a", "mod.jar")));
        Assert.True(File.Exists(PrismRecordPath(host)));
        // the terminal hook may still be RUNNING when the state flips (the emit sets the
        // state before invoking the synchronous subscribers) — and that hook writes into the
        // home tree (flint.json, launcher-state.json) that the `await using` dispose's
        // recursive Directory.Delete would race with ("Directory not empty"). The reload of
        // the instance list is the hook's LAST step, so wait for it before the scope ends.
        await WaitUntilAsync(async () =>
        {
            var instances = await GetJsonAsync(host, "/api/v1/instances");
            return instances.EnumerateArray().Any(
                instance => instance.GetProperty("id").GetString() == "one");
        }, TimeSpan.FromSeconds(15), "the fresh-target import's instance reload");
    }

    // ------------------------------------------------ G2 single-pass completion (sort+reload)

    // Synthetic accounts.json in the upstream v3 layout (AccountListTests carries the full
    // one; this is the minimal offline shape the loader accepts).
    private static string OfflineAccountsJson(string profileName) => $$"""
        {
            "accounts": [
                {
                    "profile": {
                        "capes": [],
                        "id": "cb7ecbf7173e39428f9fb92995eab3d0",
                        "name": "{{profileName}}",
                        "skin": { "id": "", "url": "", "variant": "" }
                    },
                    "type": "Offline",
                    "ygg": { "token": "0" }
                }
            ],
            "formatVersion": 3
        }
        """;

    private static async Task<JsonElement> GetJsonAsync(FlintApiHost host, string path)
    {
        using var document = JsonDocument.Parse(
            await (await host.GetAsync(path)).Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Poll <paramref name="probe"/> until it returns true (the import-finished hook
    /// runs inside the task's terminal event, but the API may report the terminal state a
    /// moment BEFORE the hook subscribers complete — the state flips first, then the
    /// synchronous handlers run).</summary>
    private static async Task WaitUntilAsync(Func<bool> probe, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.True(probe(), $"timed out waiting for {what}");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.True(await probe(), $"timed out waiting for {what}");
    }

    // C1: the import-finished hook must complete the G2 single pass — the import lands
    // prismlauncher.cfg + accounts.json at the data root, but the consumers (C5 INI
    // importer, AccountList.LoadList) ran at STARTUP, before any import existed. Without
    // the in-hook re-sort the imported settings/secrets/state keys/accounts stay invisible
    // until a restart — and a user writing any setting first would create flint.json and
    // seal the INI away forever behind the one-shot guard.
    [Fact]
    public async Task Import_Finish_Sorts_The_Ini_And_Reloads_Accounts_In_The_Same_Session()
    {
        await using var host = await FlintApiHost.StartAsync();
        var source = Path.Combine(TempRoot(), "PrismLauncher");
        Directory.CreateDirectory(Path.Combine(source, "instances", "one"));
        await File.WriteAllTextAsync(Path.Combine(source, "instances", "one", "instance.cfg"),
            "InstanceType=OneSix\n");
        Directory.CreateDirectory(Path.Combine(source, "assets", "objects", "aa"));
        await File.WriteAllTextAsync(Path.Combine(source, "assets", "objects", "aa", "blob"), "asset");
        await File.WriteAllTextAsync(Path.Combine(source, "accounts.json"), OfflineAccountsJson("ImportedOne"));
        // the recognized INI key set: record domain + secret trio + C9 state keys
        await File.WriteAllTextAsync(Path.Combine(source, "prismlauncher.cfg"), """
                                                                                [General]
                                                                                ConfigVersion=1.3
                                                                                ProxyAddr=proxy.corp
                                                                                ProxyPort=3128
                                                                                ProxyPass=hunter2
                                                                                ModrinthToken=mr-token
                                                                                TotalPlayTime=123456
                                                                                TotalPlayTimeMigrated=true
                                                                                LastOfflinePlayerName=OfflineOne
                                                                                """);

        var response = await host.PostAsync("/api/v1/migration/prism",
            new { source, mode = "copy", dryRun = false });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var taskId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("taskId").GetString()!;
        await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(60));

        // the remount half (already the pre-fix behavior, kept as the pass's first step):
        // the landed assets moved from the data root onto the store root. Poll, same
        // flip-before-hook reason — and the move is one atomic TryMoveDirectory rename on
        // the same volume, so the store blob's appearance also proves the data-root copy
        // is gone (the assert below is deterministic once the poll passes).
        await WaitUntilAsync(
            () => File.Exists(Path.Combine(host.HomeDir, "store", "assets", "objects", "aa", "blob")),
            TimeSpan.FromSeconds(15), "the import's remount");
        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "assets")));

        // the C5 sort half, SAME SESSION: /settings serves the imported values from the
        // live stores — the record domain through Config.Current, the secret through the
        // ISecretStore (no restart in between). The task state flips BEFORE the synchronous
        // hook subscribers complete (see WaitUntilAsync), so poll for the first imported
        // value: ImportIfNeeded sorts every key in ONE synchronous pass and writes the
        // flint.json seal + the state file before it returns, so ProxyAddr going live also
        // proves those two landed.
        await WaitUntilAsync(async () =>
        {
            var settings = await GetJsonAsync(host, "/api/v1/settings");
            return settings.GetProperty("groups").GetProperty("proxy")
                .GetProperty("ProxyAddr").GetString() == "proxy.corp";
        }, TimeSpan.FromSeconds(15), "the imported ProxyAddr in the live stores");
        var settings = await GetJsonAsync(host, "/api/v1/settings");
        Assert.Equal(3128,
            settings.GetProperty("groups").GetProperty("proxy").GetProperty("ProxyPort").GetInt32());
        Assert.Equal("mr-token",
            settings.GetProperty("groups").GetProperty("api").GetProperty("ModrinthToken").GetString());

        // the import wrote flint.json (the one-shot seal) and the secrets stayed OUT of it
        var configText = await File.ReadAllTextAsync(Path.Combine(host.HomeDir, "config", "flint.json"));
        Assert.DoesNotContain("hunter2", configText);
        Assert.DoesNotContain("mr-token", configText);

        // the C9 state keys landed in launcher-state.json
        var stateText = await File.ReadAllTextAsync(Path.Combine(host.HomeDir, "state", "launcher-state.json"));
        Assert.Contains("OfflineOne", stateText);
        Assert.Contains("123456", stateText);

        // the imported accounts are live in the same session — Accounts.LoadList runs AFTER
        // the C5 sort inside the same hook, so it needs its own poll (same flip-before-hook
        // reason as above)
        await WaitUntilAsync(async () =>
        {
            var accounts = await GetJsonAsync(host, "/api/v1/accounts");
            return accounts.EnumerateArray().Any(
                account => account.GetProperty("name").GetString() == "ImportedOne");
        }, TimeSpan.FromSeconds(15), "the imported accounts in the same session");

        // and the reloaded instance list discovered the imported instance too — this is the
        // hook's LAST step, so its observability is also what makes the dispose below safe
        await WaitUntilAsync(async () =>
        {
            var instances = await GetJsonAsync(host, "/api/v1/instances");
            return instances.EnumerateArray().Any(
                instance => instance.GetProperty("id").GetString() == "one");
        }, TimeSpan.FromSeconds(15), "the imported instance in the same session");
    }

    // C2: abort is a terminal state like any other — the C++ task emitted finished() after
    // emitAborted() (Task.cpp:133-146), so a cancelled import ran the same completion. The
    // port dropped finished(); before the Aborted subscription an aborted import left the
    // already-transferred assets stranded at the data root (remount never ran) and never
    // reloaded the instance list. Whitelist order: assets (dir) → instances (dir) →
    // libraries (dir) → top-level FILES — so once the instances item has landed the walk is
    // guaranteed to still be running inside the libraries bulk, and both observables below
    // are guaranteed to have landed when the abort fires.
    [Fact]
    public async Task Import_Aborted_MidFlight_Still_Completes_The_Single_Pass()
    {
        await using var host = await FlintApiHost.StartAsync();
        var source = Path.Combine(TempRoot(), "PrismLauncher");
        Directory.CreateDirectory(Path.Combine(source, "assets", "objects", "aa"));
        await File.WriteAllTextAsync(Path.Combine(source, "assets", "objects", "aa", "blob"), "asset");
        Directory.CreateDirectory(Path.Combine(source, "instances", "reloadme"));
        await File.WriteAllTextAsync(Path.Combine(source, "instances", "reloadme", "instance.cfg"),
            "InstanceType=OneSix\n");
        // the bulk keeps the per-file transfer walk busy past the abort round-trip
        for (var dir = 0; dir < 200; dir++)
        {
            Directory.CreateDirectory(Path.Combine(source, "libraries", "bulk", $"d{dir}"));
            for (var file = 0; file < 100; file++)
            {
                await File.WriteAllTextAsync(Path.Combine(source, "libraries", "bulk", $"d{dir}", $"f{file}"), "x");
            }
        }

        var response = await host.PostAsync("/api/v1/migration/prism",
            new { source, mode = "copy", dryRun = false });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var taskId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("taskId").GetString()!;

        // wait for the instances item to have landed while the bulk walk is still going
        var instanceLanded = Path.Combine(host.DataDir, "instances", "reloadme", "instance.cfg");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!File.Exists(instanceLanded))
        {
            Assert.True(DateTime.UtcNow < deadline, "the import never landed instances/reloadme");
            await Task.Delay(50);
        }

        var abort = await host.PostAsync($"/api/v1/tasks/{taskId}/abort", new { });
        Assert.True(abort.StatusCode == HttpStatusCode.NoContent,
            $"abort returned {abort.StatusCode} — the import finished before the abort landed");

        await host.WaitForTaskStateAsync(taskId, "aborted", TimeSpan.FromSeconds(30));

        // the terminal hook ran on the aborted path: the remount moved the landed assets
        // off the data root onto the store root (idempotent, safe on a partial import)…
        await WaitUntilAsync(
            () => File.Exists(Path.Combine(host.HomeDir, "store", "assets", "objects", "aa", "blob")),
            TimeSpan.FromSeconds(15), "the aborted import's remount");
        // …and the reload half made the already-landed instance visible in the same session
        await WaitUntilAsync(async () =>
        {
            var instances = await GetJsonAsync(host, "/api/v1/instances");
            return instances.EnumerateArray().Any(
                instance => instance.GetProperty("id").GetString() == "reloadme");
        }, TimeSpan.FromSeconds(15), "the aborted import's instance reload");
    }
}
