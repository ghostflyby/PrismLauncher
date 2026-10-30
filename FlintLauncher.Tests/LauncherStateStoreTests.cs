// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// launcher-state.json store tests: load-or-default over the five-record schema,
// atomic writes, corrupt-payload fallback, the path-keyed java probe map (the multi-JVM fix)
// and the round-trip through the real FlintJsonContext registration (the AOT smoke: a root
// type missing from the context is a RUNTIME failure, not a compile error).

using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.State;
using FlintLauncher.Web;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class LauncherStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-lstate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string StateFile => Path.Combine(_dir, "launcher-state.json");

    private LauncherStateStore Store() => new(new AbsolutePath(StateFile), FlintJsonContext.Default.LauncherState);

    [Fact]
    public void Missing_File_Yields_The_Schema_Default()
    {
        var store = Store();

        Assert.Equal(LauncherState.CurrentVersion, store.Current.StateVersion);
        Assert.Equal(0, store.Current.Playtime.Total);
        Assert.False(store.Current.Playtime.Migrated);
        Assert.False(store.Current.UserAskedAboutAutomaticJavaDownload);
        Assert.Equal("", store.Current.LastOfflinePlayerName);
        Assert.Empty(store.Current.Java.Detections);
        Assert.Equal([], store.Current.Ui.HiddenGroups);
        Assert.False(store.Current.Ui.UngroupedHidden);
        Assert.False(File.Exists(StateFile)); // load-only never writes
    }

    [Fact]
    public void Update_Persists_Immediately_And_RoundTrips()
    {
        var store = Store();
        store.Update(state => state with
        {
            UserAskedAboutAutomaticJavaDownload = true,
            LastOfflinePlayerName = "Steve",
            Playtime = new PlaytimeState { Total = 42, Migrated = true },
            Java = state.Java with
            {
                LastHostname = "box",
                Detections = new Dictionary<string, JavaDetectEntry>(StringComparer.Ordinal)
                {
                    ["/usr/bin/java"] = new("sig", "64", "arm64", "17.0.2", "Eclipse Adoptium", 17),
                },
            },
            Ui = new UiState { HiddenGroups = ["Mods"], UngroupedHidden = true },
        });

        // a fresh store over the same file sees the written state (the real context did the
        // serialization — camelCase property names, dictionary keys untouched)
        var reloaded = Store();
        Assert.Equal(42, reloaded.Current.Playtime.Total);
        Assert.True(reloaded.Current.Playtime.Migrated);
        Assert.True(reloaded.Current.UserAskedAboutAutomaticJavaDownload);
        Assert.Equal("Steve", reloaded.Current.LastOfflinePlayerName);
        Assert.Equal("box", reloaded.Current.Java.LastHostname);
        var entry = Assert.Single(reloaded.Current.Java.Detections);
        Assert.Equal("/usr/bin/java", entry.Key);
        Assert.Equal(new JavaDetectEntry("sig", "64", "arm64", "17.0.2", "Eclipse Adoptium", 17), entry.Value);
        Assert.Equal(["Mods"], reloaded.Current.Ui.HiddenGroups);
        Assert.True(reloaded.Current.Ui.UngroupedHidden);
    }

    [Fact]
    public void Java_Probe_Map_Keeps_Multiple_JVMs_Apart()
    {
        // the fix: the five settings keys overwrote each other — the path-keyed map
        // remembers one entry per binary
        var store = Store();
        store.Update(state => PutDetection(state, "/jvm/17/bin/java", "sig-17", "17.0.2", 17));
        store.Update(state => PutDetection(state, "/jvm/8/bin/java", "sig-8", "1.8.0_402", 8));
        // an entry from before the language version existed stays null through the fold
        store.Update(state => PutDetection(state, "/jvm/old/bin/java", "sig-old", "11.0.9", null));

        var reloaded = Store();
        Assert.Equal("17.0.2", reloaded.Current.Java.Detections["/jvm/17/bin/java"].Version);
        Assert.Equal(17, reloaded.Current.Java.Detections["/jvm/17/bin/java"].LanguageVersion);
        Assert.Equal("1.8.0_402", reloaded.Current.Java.Detections["/jvm/8/bin/java"].Version);
        Assert.Equal(8, reloaded.Current.Java.Detections["/jvm/8/bin/java"].LanguageVersion);
        Assert.Null(reloaded.Current.Java.Detections["/jvm/old/bin/java"].LanguageVersion);
    }

    private static LauncherState PutDetection(LauncherState state, string path, string signature, string version, int? languageVersion) =>
        state with
        {
            Java = state.Java with
            {
                Detections = new Dictionary<string, JavaDetectEntry>(state.Java.Detections, StringComparer.Ordinal)
                {
                    [path] = new(signature, "64", "arm64", version, "vendor", languageVersion),
                },
            },
        };

    [Fact]
    public void Corrupt_Payload_Falls_Back_To_Default_And_Keeps_The_Evidence()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StateFile, "{ not json");

        var store = Store();

        Assert.Equal(LauncherState.CurrentVersion, store.Current.StateVersion);
        Assert.Equal("{ not json", File.ReadAllText(StateFile + ".corrupt"));
    }

    [Fact]
    public void Older_Payload_Folds_Over_The_Current_Default()
    {
        Directory.CreateDirectory(_dir);
        // a v0-era payload without the java/ui halves
        File.WriteAllText(StateFile, """{"stateVersion":0,"lastOfflinePlayerName":"Alex"}""");

        var store = Store();

        Assert.Equal(LauncherState.CurrentVersion, store.Current.StateVersion);
        Assert.Equal("Alex", store.Current.LastOfflinePlayerName);
        // the added members took their defaults through the merge
        Assert.Empty(store.Current.Java.Detections);
        Assert.Equal([], store.Current.Ui.HiddenGroups);

        // and the next update rewrites the file at the current version
        store.Update(state => state with { LastOfflinePlayerName = "Alex" });
        using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
        Assert.Equal(LauncherState.CurrentVersion, doc.RootElement.GetProperty("stateVersion").GetInt32());
    }

    [Fact]
    public void Newer_Payload_KeepsIts_OnDisk_VersionStamp()
    {
        Directory.CreateDirectory(_dir);
        // a downgrade: this build reads a newer state file — the unknown members are already
        // lost at deserialize, so the store must NOT re-stamp the file as the current version
        File.WriteAllText(StateFile, """{"stateVersion":9,"lastOfflinePlayerName":"Future"}""");

        var store = Store();
        Assert.Equal(9, store.Current.StateVersion);
        Assert.Equal("Future", store.Current.LastOfflinePlayerName);

        // updates preserve the on-disk (newer) stamp — the file stays visibly v9, not v1
        store.Update(state => state with { LastOfflinePlayerName = "Future" });
        using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
        Assert.Equal(9, doc.RootElement.GetProperty("stateVersion").GetInt32());

        // and a reload stays stable (v9 folds to v9 again, not to the current version)
        Assert.Equal(9, Store().Current.StateVersion);
    }
}

/// <summary>LaunchController's offline-name write-back: the used name lands in
/// launcher-state.json — not in settings — and the next headless offline launch suggests it.
/// The controller is driven through the real Login flow; the launch itself fails fast
/// (no JavaCheck.jar in the fixture install root), which is fine — the name decision
/// happens before the launch task is built.</summary>
public class LaunchControllerOfflineNameStateTests
{
    [Fact]
    public async Task OfflineName_IsStoredInState_AndSuggestedNextTime()
    {
        using var fixture = new LaunchFixture();

        // no OfflineName and no stored name: the headless suggestion is the placeholder.
        // The controller task only terminates with the game session, so poll for the name
        // decision (it happens before the launch task is built) and abort the pipeline.
        var instance = await fixture.CreateInstance();
        var first = new LaunchController
        {
            Instance = instance,
            WantedLaunchMode = LaunchMode.Offline,
            Services = fixture.CreateServices(),
        };
        using var firstRun = new CancellationTokenSource();
        var firstTask = first.RunAsync(firstRun.Token);
        Assert.Equal("Player", await WaitForOfflineName(fixture, expected: "Player"));
        first.Abort();
        await firstRun.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask);

        // the handler (dialog) path: the suggestion prefills from the stored name (C++
        // LastOfflinePlayerName parity) and the picked name is written back
        string? capturedSuggestion = null;
        var second = new LaunchController
        {
            Instance = instance,
            WantedLaunchMode = LaunchMode.Offline,
            Services = fixture.CreateServices(),
            AskOfflineNameHandler = (_, _, suggestion) =>
            {
                capturedSuggestion = suggestion;
                return "HandlerPicked";
            },
        };
        using var secondRun = new CancellationTokenSource();
        var secondTask = second.RunAsync(secondRun.Token);
        Assert.Equal("Player", capturedSuggestion);
        Assert.Equal("HandlerPicked", await WaitForOfflineName(fixture, expected: "HandlerPicked"));
        second.Abort();
        await secondRun.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);
    }

    private static async Task<string> WaitForOfflineName(LaunchFixture fixture, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (fixture.State.Current.LastOfflinePlayerName != expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"offline name never became '{expected}' (last: '{fixture.State.Current.LastOfflinePlayerName}')");
            }

            await Task.Delay(20);
        }

        return fixture.State.Current.LastOfflinePlayerName;
    }
}
