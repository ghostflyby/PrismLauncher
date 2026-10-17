// SPDX-License-Identifier: GPL-3.0-only
// TypedJsonFile<T> infrastructure tests (GLOBAL §1): load-or-default, the record-merge
// hook, atomic writes, and the stateVersion evolution gate. The sample record uses a
// test-local source-generated context — production consumers pass FlintJsonContext's
// JsonTypeInfo the same way.

using System.Text.Json.Serialization;
using FlintLauncher.Core;
using Xunit;

namespace FlintLauncher.Tests;

public class TypedJsonFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-tjf-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string FilePath(string name = "state.json") => Path.Combine(_dir, name);

    private TypedJsonFile<SampleState> File(string name = "state.json") =>
        new(FilePath(name), TypedJsonTestContext.Default.SampleState);

    // ------------------------------------------------------------------ load-or-default

    [Fact]
    public void Missing_File_Yields_The_Factory_Default()
    {
        var called = 0;
        var loaded = File().LoadOrDefault(() =>
        {
            called++;
            return new SampleState { StateVersion = 2, Name = "default" };
        });

        Assert.Equal(1, called);
        Assert.Equal("default", loaded.Name);
        Assert.False(System.IO.File.Exists(FilePath()));
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_The_Record()
    {
        var file = File();
        file.Save(new SampleState { StateVersion = 2, Name = "saved", Counter = 7 });

        var loaded = file.LoadOrDefault(() => new SampleState { StateVersion = 2 });
        Assert.Equal("saved", loaded.Name);
        Assert.Equal(7, loaded.Counter);
        Assert.Equal(2, loaded.StateVersion);
    }

    [Fact]
    public void Corrupt_Payload_Falls_Back_To_Default_And_Keeps_The_Evidence()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(FilePath(), "{ not json");

        var loaded = File().LoadOrDefault(() => new SampleState { StateVersion = 2, Name = "default" });

        Assert.Equal("default", loaded.Name);
        Assert.Equal("{ not json", System.IO.File.ReadAllText(FilePath() + ".corrupt"));
    }

    // ------------------------------------------------------- record merge (with-expr fold)

    [Fact]
    public void Merge_Hook_Folds_The_Loaded_Record_Over_A_Fresh_Default()
    {
        Directory.CreateDirectory(_dir);
        // a payload written before "Counter" existed in the schema
        System.IO.File.WriteAllText(FilePath(), """{"stateVersion":2,"name":"old"}""");

        var loaded = File().LoadOrDefault(
            () => new SampleState { StateVersion = 2 },
            // the AOT-safe merge shape: a with-expression against a current default
            existing => existing with { Counter = new SampleState { StateVersion = 2 }.Counter + 42 });

        Assert.Equal("old", loaded.Name);
        Assert.Equal(42, loaded.Counter);
    }

    // ------------------------------------------------------------------ atomic write

    [Fact]
    public void Save_Leaves_No_Temp_File_Behind_And_Overwrites_In_Place()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(FilePath(), "previous contents");
        var file = File();

        file.Save(new SampleState { StateVersion = 2, Name = "first" });
        file.Save(new SampleState { StateVersion = 2, Name = "second" });

        var loaded = file.LoadOrDefault(() => new SampleState { StateVersion = 2 });
        Assert.Equal("second", loaded.Name);
        var leftovers = Directory.EnumerateFiles(_dir).ToList();
        Assert.Equal([FilePath()], leftovers);
    }

    [Fact]
    public void Save_Creates_Missing_Parent_Directories()
    {
        var file = new TypedJsonFile<SampleState>(Path.Combine(_dir, "nested", "deeper", "state.json"),
            TypedJsonTestContext.Default.SampleState);

        file.Save(new SampleState { StateVersion = 2, Name = "deep" });

        Assert.Equal("deep", file.LoadOrDefault(() => new SampleState { StateVersion = 2 }).Name);
    }

    [Fact]
    public void Save_Respects_The_Requested_Unix_File_Mode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var file = File("private.json");
        file.Save(new SampleState { StateVersion = 2 }, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var mode = System.IO.File.GetUnixFileMode(FilePath("private.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    // ------------------------------------------------------------- stateVersion evolution

    [Fact]
    public void Evolution_Gate_Passes_An_Equal_Version_Through_Untouched()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(FilePath(), """{"stateVersion":2,"name":"current"}""");

        var evolveCalled = false;
        var loaded = File().LoadEvolved(() => new SampleState { StateVersion = 2 },
            (_, payload) =>
            {
                evolveCalled = true;
                return payload;
            });

        Assert.False(evolveCalled);
        Assert.Equal("current", loaded.Name);
    }

    [Fact]
    public void Evolution_Gate_Hands_Older_Payloads_To_The_Migration_Function()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(FilePath(), """{"stateVersion":1,"name":"legacy","counter":9}""");

        var loaded = File().LoadEvolved(() => new SampleState { StateVersion = 2 },
            (onDiskVersion, payload) => new SampleState
            {
                StateVersion = 2,
                Name = $"{payload.Name}@v{onDiskVersion}",
                Counter = payload.Counter,
            });

        Assert.Equal("legacy@v1", loaded.Name);
        Assert.Equal(9, loaded.Counter);
    }

    [Fact]
    public void Evolution_Gate_Defaults_A_Missing_File_Without_Evolving()
    {
        var evolveCalled = false;
        var loaded = File().LoadEvolved(() => new SampleState { StateVersion = 3, Name = "fresh" },
            (_, payload) =>
            {
                evolveCalled = true;
                return payload;
            });

        Assert.False(evolveCalled);
        Assert.Equal("fresh", loaded.Name);
        Assert.Equal(3, loaded.StateVersion);
    }

    [Fact]
    public void Evolution_Gate_Surfaces_Newer_On_Disk_Versions_To_The_Caller()
    {
        Directory.CreateDirectory(_dir);
        // a downgrade: the payload is from a newer schema than this build knows
        System.IO.File.WriteAllText(FilePath(), """{"stateVersion":9,"name":"future"}""");

        var seenVersion = 0;
        var loaded = File().LoadEvolved(() => new SampleState { StateVersion = 2 },
            (onDiskVersion, payload) =>
            {
                seenVersion = onDiskVersion;
                return payload with { Name = "demoted" };
            });

        Assert.Equal(9, seenVersion);
        Assert.Equal("demoted", loaded.Name);
    }
}

/// <summary>Test state record: the S5 launcher-state.json shape in miniature.</summary>
internal sealed record SampleState : IStateVersioned
{
    public required int StateVersion { get; init; }
    public string Name { get; init; } = "";
    public int Counter { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SampleState))]
internal sealed partial class TypedJsonTestContext : JsonSerializerContext;
