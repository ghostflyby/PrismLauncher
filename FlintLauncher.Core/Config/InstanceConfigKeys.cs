// SPDX-License-Identifier: GPL-3.0-only
// The instance-side key registry (D4/C6/C7 — the instance.json counterpart of
// FlintConfigKeys): every instance settings key maps to its record member exactly once.
//
// Resolution shape: Get(user, instance) resolves a key as instance-layer member ?? user-layer
// (flint.json) member — the C7 fold — for the layered keys, or reads the instance layer alone
// for the instance-only keys (their defaults stay in the BaseInstance/MinecraftInstance
// Setting registrations, which Setting.Get falls back to when this getter yields null).
//
// The Override* gate booleans DEGENERATE (C7): reading one yields "any member of its family
// is non-null at the instance layer"; writing false CLEARS the family (reset to inherit) and
// writing true is a no-op (the member write is what makes the override real — exactly how the
// old GUI/tasks used the gate+value pair). The INI importer consults the same gate keys to
// drop gate=false values into inheritance.
//
// The runtime time trio (lastLaunchTime/lastTimePlayed/totalTimePlayed) is NOT here — it
// lives in instance-state.json (D4) and is routed by the settings adapter directly.
//
// The C7 passthrough pair (ConsoleMaxLines/ConsoleOverflowStop) is deliberately global-only:
// reads come from the user layer, instance-side writes are dropped (non-layered per C7).

using FlintLauncher.Core.Json;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Config;

/// <summary>
///     One instance settings key: the gate family it belongs to — a derived gate lists
///     its own family (the OverrideLaunchCmd synonym lists OverrideCommands); the INI spellings
///     that set each family live in <see cref="GateIniSpellings" /> (null = instance-only or
///     global-only). Plus the resolved getter over (user, instance), the instance-layer set
///     probe, and the typed setter applied immutably over the instance payload (null value =
///     reset to inherit; gate keys: false/null = clear the family, true = no-op).
/// </summary>
public sealed record InstanceKeyEntry(
    string Key,
    string? GateKey,
    Func<FlintConfigFile, InstanceConfigFile, object?> Get,
    Func<InstanceConfigFile, bool> IsSet,
    Func<InstanceConfigFile, object?, InstanceConfigFile> Apply);

public static class InstanceConfigKeys
{
    private static readonly Dictionary<string, InstanceKeyEntry> AllMap = Build();

    /// <summary>
    ///     The INI spellings that SET a gate family → the canonical family (the C++
    ///     synonym pair OverrideCommands/OverrideLaunchCmd included). The INI importer collects
    ///     gate values ONLY from these keys — never from a family's member keys. This matters
    ///     because QSettings writes instance.cfg keys in ordinal order, so MaxMemAlloc /
    ///     JavaPath / JvmArgs sort BEFORE their Override* gate: a collector that treats any
    ///     GateKey-carrying key as a gate source would read AsBool("8192")=false and silently
    ///     drop the most common memory/java overrides (and, reversed, let a value key pollute a
    ///     gate the cfg explicitly set to false).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GateIniSpellings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OverrideJavaLocation"] = "OverrideJavaLocation",
            ["OverrideJavaArgs"] = "OverrideJavaArgs",
            ["OverrideMemory"] = "OverrideMemory",
            ["OverrideWindow"] = "OverrideWindow",
            ["OverrideCommands"] = "OverrideCommands",
            ["OverrideLaunchCmd"] = "OverrideCommands",
            ["OverrideConsole"] = "OverrideConsole",
            ["OverrideGameTime"] = "OverrideGameTime",
            ["OverrideEnv"] = "OverrideEnv",
            ["OverridePerformance"] = "OverridePerformance",
            ["OverrideNativeWorkarounds"] = "OverrideNativeWorkarounds",
            ["OverrideMiscellaneous"] = "OverrideMiscellaneous",
            ["OverrideLegacySettings"] = "OverrideLegacySettings",
            ["OverrideModDownloadLoaders"] = "OverrideModDownloadLoaders"
        };

    /// <summary>
    ///     Every known instance key, ordered by name (deterministic enumeration for the
    ///     settings endpoint).
    /// </summary>
    public static IReadOnlyCollection<InstanceKeyEntry> All => AllMap.Values;

    public static InstanceKeyEntry? Resolve(string key) => AllMap.GetValueOrDefault(key);

    // ------------------------------------------------------------------ construction helpers

    private static InstanceKeyEntry E(string key, string? gate,
        Func<FlintConfigFile, InstanceConfigFile, object?> get,
        Func<InstanceConfigFile, bool> isSet,
        Func<InstanceConfigFile, object?, InstanceConfigFile> apply) => new(key, gate, get, isSet, apply);

    /// <summary>
    ///     null-preserving coercions: a null write RESETS the member to "not set"
    ///     (inherit) — coercing first would materialize zero/false/"" as a real override.
    /// </summary>
    private static int? IntOf(object? v) => v is null ? null : Variant.AsInt(v);

    private static bool? BoolOf(object? v) => v is null ? null : Variant.AsBool(v);

    private static string? StringOf(object? v) => v is null ? null : Variant.AsString(v);

    /// <summary>
    ///     Boxed-member set probe: a member counts as set when its (boxed) value is
    ///     non-null — int?/string?/bool? all box, null stays null.
    /// </summary>
    private static bool Any(params object?[] members)
    {
        foreach (var member in members)
            if (member is not null)
                return true;

        return false;
    }

    // ------------------------------------------------------------- gate getters / clears

    private static bool GateJavaLocation(InstanceConfigFile i) =>
        Any(i.Launch?.Java?.JavaPath, i.Launch?.Java?.IgnoreJavaCompatibility);

    private static bool GateJavaArgs(InstanceConfigFile i) => i.Launch?.Java?.JvmArgs is not null;

    private static bool GateMemory(InstanceConfigFile i) => Any(
        i.Launch?.Memory?.MinMemAlloc, i.Launch?.Memory?.MaxMemAlloc,
        i.Launch?.Memory?.LowMemWarning);

    private static bool GateWindow(InstanceConfigFile i) => Any(
        i.Launch?.Window?.LaunchMaximized, i.Launch?.Window?.MinecraftWinWidth,
        i.Launch?.Window?.MinecraftWinHeight);

    private static bool GateCommands(InstanceConfigFile i) => Any(
        i.Launch?.Commands?.PreLoadCommand, i.Launch?.Commands?.PreLaunchCommand,
        i.Launch?.Commands?.WrapperCommand, i.Launch?.Commands?.PostExitCommand);

    private static bool GateEnv(InstanceConfigFile i) => i.Launch?.Env is not null;

    private static bool GatePerformance(InstanceConfigFile i) => Any(
        i.Launch?.Performance?.EnableFeralGamemode, i.Launch?.Performance?.EnableMangoHud,
        i.Launch?.Performance?.UseDiscreteGpu, i.Launch?.Performance?.UseZink);

    private static bool GateNativeLib(InstanceConfigFile i) => Any(
        i.Launch?.NativeLib?.UseNativeOpenAl, i.Launch?.NativeLib?.CustomOpenAlPath,
        i.Launch?.NativeLib?.UseNativeGlfw, i.Launch?.NativeLib?.CustomGlfwPath,
        i.Launch?.NativeLib?.UseNativeSdl, i.Launch?.NativeLib?.CustomSdlPath);

    private static bool GateConsole(InstanceConfigFile i) => Any(
        i.Misc?.Console?.ShowConsole, i.Misc?.Console?.AutoCloseConsole,
        i.Misc?.Console?.ShowConsoleOnError, i.Misc?.Console?.LogPrePostOutput);

    private static bool GateGameTime(InstanceConfigFile i) => Any(
        i.Misc?.GameTime?.ShowGameTime, i.Misc?.GameTime?.RecordGameTime);

    private static bool GateMiscellaneous(InstanceConfigFile i) => Any(
        i.Misc?.CloseAfterLaunch, i.Misc?.QuitAfterGameStop);

    private static bool GateLegacySettings(InstanceConfigFile i) => i.Misc?.OnlineFixes is not null;

    private static bool GateModDownloadLoaders(InstanceConfigFile i) => i.Misc?.ModDownloadLoaders is not null;

    /// <summary>
    ///     The gate write: false/null clears the family (C7 — the members fall back to
    ///     inheritance); true is a no-op (the family is derived from its members).
    /// </summary>
    private static InstanceConfigFile GateWrite(InstanceConfigFile i, object? value,
        Func<InstanceConfigFile, bool> derived, Func<InstanceConfigFile, InstanceConfigFile> clear) =>
        Variant.AsBool(value) ? i : clear(i);

    // ---- explicit family clears (with-expressions only — no reflection, AOT)

    private static InstanceConfigFile ClearJavaLocation(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with
        {
            Java = (i.Launch?.Java ?? new InstanceJavaConfig()) with { JavaPath = null, IgnoreJavaCompatibility = null }
        }
    };

    private static InstanceConfigFile ClearJavaArgs(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with
        {
            Java = (i.Launch?.Java ?? new InstanceJavaConfig()) with { JvmArgs = null }
        }
    };

    private static InstanceConfigFile ClearMemory(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with { Memory = new InstanceMemoryConfig() }
    };

    private static InstanceConfigFile ClearWindow(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with { Window = new InstanceWindowConfig() }
    };

    private static InstanceConfigFile ClearCommands(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with { Commands = new InstanceCommandsConfig() }
    };

    private static InstanceConfigFile ClearEnv(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with { Env = null }
    };

    private static InstanceConfigFile ClearPerformance(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with { Performance = new InstancePerformanceConfig() }
    };

    private static InstanceConfigFile ClearNativeLib(InstanceConfigFile i) => i with
    {
        Launch = (i.Launch ?? new InstanceLaunchConfig()) with { NativeLib = new InstanceNativeLibConfig() }
    };

    private static InstanceConfigFile ClearConsole(InstanceConfigFile i) => i with
    {
        Misc = (i.Misc ?? new InstanceMiscConfig()) with { Console = null }
    };

    private static InstanceConfigFile ClearGameTime(InstanceConfigFile i) => i with
    {
        Misc = (i.Misc ?? new InstanceMiscConfig()) with { GameTime = null }
    };

    private static InstanceConfigFile ClearMiscellaneous(InstanceConfigFile i) => i with
    {
        Misc = (i.Misc ?? new InstanceMiscConfig()) with { CloseAfterLaunch = null, QuitAfterGameStop = null }
    };

    private static InstanceConfigFile ClearLegacySettings(InstanceConfigFile i) => i with
    {
        Misc = (i.Misc ?? new InstanceMiscConfig()) with { OnlineFixes = null }
    };

    private static InstanceConfigFile ClearModDownloadLoaders(InstanceConfigFile i) => i with
    {
        Misc = (i.Misc ?? new InstanceMiscConfig()) with { ModDownloadLoaders = null }
    };

    // ------------------------------------------------------------------------ the registry

    private static Dictionary<string, InstanceKeyEntry> Build()
    {
        List<InstanceKeyEntry> entries =
        [
            // ---- identity (instance-only; defaults via the Setting registrations)
            E("uuid", null,
                (_, i) => i.Identity?.Uuid,
                i => i.Identity?.Uuid is not null,
                (i, v) => i with { Identity = (i.Identity ?? new InstanceIdentityConfig()) with { Uuid = StringOf(v) } }),
            E("name", null,
                (_, i) => i.Identity?.Name,
                i => i.Identity?.Name is not null,
                (i, v) => i with { Identity = (i.Identity ?? new InstanceIdentityConfig()) with { Name = StringOf(v) } }),
            E("iconKey", null,
                (_, i) => i.Identity?.IconKey,
                i => i.Identity?.IconKey is not null,
                (i, v) => i with { Identity = (i.Identity ?? new InstanceIdentityConfig()) with { IconKey = StringOf(v) } }),
            E("notes", null,
                (_, i) => i.Identity?.Notes,
                i => i.Identity?.Notes is not null,
                (i, v) => i with { Identity = (i.Identity ?? new InstanceIdentityConfig()) with { Notes = StringOf(v) } }),
            E("shortcuts", null,
                (_, i) => i.Identity?.Shortcuts,
                i => i.Identity?.Shortcuts is not null,
                (i, v) => i with { Identity = (i.Identity ?? new InstanceIdentityConfig()) with { Shortcuts = StringOf(v) } }),

            // ---- java (OverrideJavaLocation: path + compatibility; OverrideJavaArgs: args)
            E("JavaPath", "OverrideJavaLocation",
                (u, i) => i.Launch?.Java?.JavaPath ?? u.Java!.JavaPath,
                i => i.Launch?.Java?.JavaPath is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Java = (i.Launch?.Java ?? new InstanceJavaConfig()) with { JavaPath = StringOf(v) }
                    }
                }),
            E("IgnoreJavaCompatibility", "OverrideJavaLocation",
                (u, i) => i.Launch?.Java?.IgnoreJavaCompatibility ?? u.Java!.IgnoreJavaCompatibility,
                i => i.Launch?.Java?.IgnoreJavaCompatibility is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Java = (i.Launch?.Java ?? new InstanceJavaConfig()) with { IgnoreJavaCompatibility = BoolOf(v) }
                    }
                }),
            E("JvmArgs", "OverrideJavaArgs",
                (u, i) => i.Launch?.Java?.JvmArgs ?? u.Java!.JvmArgs,
                i => i.Launch?.Java?.JvmArgs is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Java = (i.Launch?.Java ?? new InstanceJavaConfig()) with { JvmArgs = StringOf(v) }
                    }
                }),
            E("AutomaticJava", null,
                (_, i) => i.Launch?.Java?.AutomaticJava,
                i => i.Launch?.Java?.AutomaticJava is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Java = (i.Launch?.Java ?? new InstanceJavaConfig()) with { AutomaticJava = BoolOf(v) }
                    }
                }),

            // ---- memory (OverrideMemory)
            E("MinMemAlloc", "OverrideMemory",
                (u, i) => i.Launch?.Memory?.MinMemAlloc ?? u.Memory!.MinMemAlloc,
                i => i.Launch?.Memory?.MinMemAlloc is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Memory = (i.Launch?.Memory ?? new InstanceMemoryConfig()) with { MinMemAlloc = IntOf(v) }
                    }
                }),
            E("MaxMemAlloc", "OverrideMemory",
                (u, i) => i.Launch?.Memory?.MaxMemAlloc ?? u.Memory!.MaxMemAlloc,
                i => i.Launch?.Memory?.MaxMemAlloc is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Memory = (i.Launch?.Memory ?? new InstanceMemoryConfig()) with { MaxMemAlloc = IntOf(v) }
                    }
                }),
            // PermGen died with the Java <8 support floor (2026-10-05): an imported
            // instance.cfg PermGen key is now an unknown key and drops silently.
            E("LowMemWarning", "OverrideMemory",
                (u, i) => i.Launch?.Memory?.LowMemWarning ?? u.Memory!.LowMemWarning,
                i => i.Launch?.Memory?.LowMemWarning is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Memory = (i.Launch?.Memory ?? new InstanceMemoryConfig()) with { LowMemWarning = BoolOf(v) }
                    }
                }),

            // ---- window (OverrideWindow)
            E("LaunchMaximized", "OverrideWindow",
                (u, i) => i.Launch?.Window?.LaunchMaximized ?? u.Window!.LaunchMaximized,
                i => i.Launch?.Window?.LaunchMaximized is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Window = (i.Launch?.Window ?? new InstanceWindowConfig()) with { LaunchMaximized = BoolOf(v) }
                    }
                }),
            E("MinecraftWinWidth", "OverrideWindow",
                (u, i) => i.Launch?.Window?.MinecraftWinWidth ?? u.Window!.MinecraftWinWidth,
                i => i.Launch?.Window?.MinecraftWinWidth is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Window = (i.Launch?.Window ?? new InstanceWindowConfig()) with { MinecraftWinWidth = IntOf(v) }
                    }
                }),
            E("MinecraftWinHeight", "OverrideWindow",
                (u, i) => i.Launch?.Window?.MinecraftWinHeight ?? u.Window!.MinecraftWinHeight,
                i => i.Launch?.Window?.MinecraftWinHeight is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Window = (i.Launch?.Window ?? new InstanceWindowConfig()) with { MinecraftWinHeight = IntOf(v) }
                    }
                }),

            // ---- commands (OverrideCommands, synonym OverrideLaunchCmd)
            E("PreLoadCommand", "OverrideCommands",
                (u, i) => i.Launch?.Commands?.PreLoadCommand ?? u.Commands!.PreLoadCommand,
                i => i.Launch?.Commands?.PreLoadCommand is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Commands = (i.Launch?.Commands ?? new InstanceCommandsConfig()) with { PreLoadCommand = StringOf(v) }
                    }
                }),
            E("PreLaunchCommand", "OverrideCommands",
                (u, i) => i.Launch?.Commands?.PreLaunchCommand ?? u.Commands!.PreLaunchCommand,
                i => i.Launch?.Commands?.PreLaunchCommand is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Commands = (i.Launch?.Commands ?? new InstanceCommandsConfig()) with { PreLaunchCommand = StringOf(v) }
                    }
                }),
            E("WrapperCommand", "OverrideCommands",
                (u, i) => i.Launch?.Commands?.WrapperCommand ?? u.Commands!.WrapperCommand,
                i => i.Launch?.Commands?.WrapperCommand is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Commands = (i.Launch?.Commands ?? new InstanceCommandsConfig()) with { WrapperCommand = StringOf(v) }
                    }
                }),
            E("PostExitCommand", "OverrideCommands",
                (u, i) => i.Launch?.Commands?.PostExitCommand ?? u.Commands!.PostExitCommand,
                i => i.Launch?.Commands?.PostExitCommand is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Commands = (i.Launch?.Commands ?? new InstanceCommandsConfig()) with { PostExitCommand = StringOf(v) }
                    }
                }),

            // ---- env (OverrideEnv): the custom environment map's JSON text
            E("Env", "OverrideEnv",
                (u, i) => i.Launch?.Env ?? u.MinecraftMisc!.Env,
                i => i.Launch?.Env is not null,
                (i, v) => i with { Launch = (i.Launch ?? new InstanceLaunchConfig()) with { Env = StringOf(v) } }),

            // ---- performance (OverridePerformance)
            E("EnableFeralGamemode", "OverridePerformance",
                (u, i) => i.Launch?.Performance?.EnableFeralGamemode ?? u.Performance!.EnableFeralGamemode,
                i => i.Launch?.Performance?.EnableFeralGamemode is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Performance = (i.Launch?.Performance ?? new InstancePerformanceConfig()) with
                        {
                            EnableFeralGamemode = BoolOf(v)
                        }
                    }
                }),
            E("EnableMangoHud", "OverridePerformance",
                (u, i) => i.Launch?.Performance?.EnableMangoHud ?? u.Performance!.EnableMangoHud,
                i => i.Launch?.Performance?.EnableMangoHud is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Performance = (i.Launch?.Performance ?? new InstancePerformanceConfig()) with
                        {
                            EnableMangoHud = BoolOf(v)
                        }
                    }
                }),
            E("UseDiscreteGpu", "OverridePerformance",
                (u, i) => i.Launch?.Performance?.UseDiscreteGpu ?? u.Performance!.UseDiscreteGpu,
                i => i.Launch?.Performance?.UseDiscreteGpu is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Performance = (i.Launch?.Performance ?? new InstancePerformanceConfig()) with
                        {
                            UseDiscreteGpu = BoolOf(v)
                        }
                    }
                }),
            E("UseZink", "OverridePerformance",
                (u, i) => i.Launch?.Performance?.UseZink ?? u.Performance!.UseZink,
                i => i.Launch?.Performance?.UseZink is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        Performance = (i.Launch?.Performance ?? new InstancePerformanceConfig()) with { UseZink = BoolOf(v) }
                    }
                }),

            // ---- native library workarounds (OverrideNativeWorkarounds)
            E("UseNativeOpenAL", "OverrideNativeWorkarounds",
                (u, i) => i.Launch?.NativeLib?.UseNativeOpenAl ?? u.NativeLib!.UseNativeOpenAl,
                i => i.Launch?.NativeLib?.UseNativeOpenAl is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        NativeLib = (i.Launch?.NativeLib ?? new InstanceNativeLibConfig()) with
                        {
                            UseNativeOpenAl = BoolOf(v)
                        }
                    }
                }),
            E("CustomOpenALPath", "OverrideNativeWorkarounds",
                (u, i) => i.Launch?.NativeLib?.CustomOpenAlPath ?? u.NativeLib!.CustomOpenAlPath,
                i => i.Launch?.NativeLib?.CustomOpenAlPath is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        NativeLib = (i.Launch?.NativeLib ?? new InstanceNativeLibConfig()) with
                        {
                            CustomOpenAlPath = StringOf(v)
                        }
                    }
                }),
            E("UseNativeGLFW", "OverrideNativeWorkarounds",
                (u, i) => i.Launch?.NativeLib?.UseNativeGlfw ?? u.NativeLib!.UseNativeGlfw,
                i => i.Launch?.NativeLib?.UseNativeGlfw is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        NativeLib = (i.Launch?.NativeLib ?? new InstanceNativeLibConfig()) with
                        {
                            UseNativeGlfw = BoolOf(v)
                        }
                    }
                }),
            E("CustomGLFWPath", "OverrideNativeWorkarounds",
                (u, i) => i.Launch?.NativeLib?.CustomGlfwPath ?? u.NativeLib!.CustomGlfwPath,
                i => i.Launch?.NativeLib?.CustomGlfwPath is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        NativeLib = (i.Launch?.NativeLib ?? new InstanceNativeLibConfig()) with
                        {
                            CustomGlfwPath = StringOf(v)
                        }
                    }
                }),
            E("UseNativeSDL", "OverrideNativeWorkarounds",
                (u, i) => i.Launch?.NativeLib?.UseNativeSdl ?? u.NativeLib!.UseNativeSdl,
                i => i.Launch?.NativeLib?.UseNativeSdl is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        NativeLib = (i.Launch?.NativeLib ?? new InstanceNativeLibConfig()) with
                        {
                            UseNativeSdl = BoolOf(v)
                        }
                    }
                }),
            E("CustomSDLPath", "OverrideNativeWorkarounds",
                (u, i) => i.Launch?.NativeLib?.CustomSdlPath ?? u.NativeLib!.CustomSdlPath,
                i => i.Launch?.NativeLib?.CustomSdlPath is not null,
                (i, v) => i with
                {
                    Launch = (i.Launch ?? new InstanceLaunchConfig()) with
                    {
                        NativeLib = (i.Launch?.NativeLib ?? new InstanceNativeLibConfig()) with
                        {
                            CustomSdlPath = StringOf(v)
                        }
                    }
                }),

            // ---- console (OverrideConsole) — the C7 passthrough pair is NOT here
            E("ShowConsole", "OverrideConsole",
                (u, i) => i.Misc?.Console?.ShowConsole ?? u.Console!.ShowConsole,
                i => i.Misc?.Console?.ShowConsole is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with
                    {
                        Console = (i.Misc?.Console ?? new InstanceConsoleConfig()) with { ShowConsole = BoolOf(v) }
                    }
                }),
            E("AutoCloseConsole", "OverrideConsole",
                (u, i) => i.Misc?.Console?.AutoCloseConsole ?? u.Console!.AutoCloseConsole,
                i => i.Misc?.Console?.AutoCloseConsole is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with
                    {
                        Console = (i.Misc?.Console ?? new InstanceConsoleConfig()) with { AutoCloseConsole = BoolOf(v) }
                    }
                }),
            E("ShowConsoleOnError", "OverrideConsole",
                (u, i) => i.Misc?.Console?.ShowConsoleOnError ?? u.Console!.ShowConsoleOnError,
                i => i.Misc?.Console?.ShowConsoleOnError is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with
                    {
                        Console = (i.Misc?.Console ?? new InstanceConsoleConfig()) with { ShowConsoleOnError = BoolOf(v) }
                    }
                }),
            E("LogPrePostOutput", "OverrideConsole",
                (u, i) => i.Misc?.Console?.LogPrePostOutput ?? u.Console!.LogPrePostOutput,
                i => i.Misc?.Console?.LogPrePostOutput is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with
                    {
                        Console = (i.Misc?.Console ?? new InstanceConsoleConfig()) with { LogPrePostOutput = BoolOf(v) }
                    }
                }),

            // ---- game time (OverrideGameTime)
            E("ShowGameTime", "OverrideGameTime",
                (u, i) => i.Misc?.GameTime?.ShowGameTime ?? u.GameTime!.ShowGameTime,
                i => i.Misc?.GameTime?.ShowGameTime is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with
                    {
                        GameTime = (i.Misc?.GameTime ?? new InstanceGameTimeConfig()) with { ShowGameTime = BoolOf(v) }
                    }
                }),
            E("RecordGameTime", "OverrideGameTime",
                (u, i) => i.Misc?.GameTime?.RecordGameTime ?? u.GameTime!.RecordGameTime,
                i => i.Misc?.GameTime?.RecordGameTime is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with
                    {
                        GameTime = (i.Misc?.GameTime ?? new InstanceGameTimeConfig()) with { RecordGameTime = BoolOf(v) }
                    }
                }),

            // ---- miscellaneous gates (OverrideMiscellaneous / OverrideLegacySettings /
            //      OverrideModDownloadLoaders)
            E("CloseAfterLaunch", "OverrideMiscellaneous",
                (u, i) => i.Misc?.CloseAfterLaunch ?? u.Launcher!.CloseAfterLaunch,
                i => i.Misc?.CloseAfterLaunch is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { CloseAfterLaunch = BoolOf(v) }
                }),
            E("QuitAfterGameStop", "OverrideMiscellaneous",
                (u, i) => i.Misc?.QuitAfterGameStop ?? u.Launcher!.QuitAfterGameStop,
                i => i.Misc?.QuitAfterGameStop is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { QuitAfterGameStop = BoolOf(v) }
                }),
            E("OnlineFixes", "OverrideLegacySettings",
                (u, i) => i.Misc?.OnlineFixes ?? u.MinecraftMisc!.OnlineFixes,
                i => i.Misc?.OnlineFixes is not null,
                (i, v) => i with { Misc = (i.Misc ?? new InstanceMiscConfig()) with { OnlineFixes = BoolOf(v) } }),
            E("ModDownloadLoaders", "OverrideModDownloadLoaders",
                (_, i) => i.Misc?.ModDownloadLoaders,
                i => i.Misc?.ModDownloadLoaders is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { ModDownloadLoaders = StringOf(v) }
                }),

            // ---- instance-only misc keys
            E("JoinServerOnLaunch", null,
                (_, i) => i.Misc?.JoinServerOnLaunch,
                i => i.Misc?.JoinServerOnLaunch is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { JoinServerOnLaunch = BoolOf(v) }
                }),
            E("JoinServerOnLaunchAddress", null,
                (_, i) => i.Misc?.JoinServerOnLaunchAddress,
                i => i.Misc?.JoinServerOnLaunchAddress is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { JoinServerOnLaunchAddress = StringOf(v) }
                }),
            E("JoinWorldOnLaunch", null,
                (_, i) => i.Misc?.JoinWorldOnLaunch,
                i => i.Misc?.JoinWorldOnLaunch is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { JoinWorldOnLaunch = StringOf(v) }
                }),
            E("UseAccountForInstance", null,
                (_, i) => i.Misc?.UseAccountForInstance,
                i => i.Misc?.UseAccountForInstance is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { UseAccountForInstance = BoolOf(v) }
                }),
            E("InstanceAccountId", null,
                (_, i) => i.Misc?.InstanceAccountId,
                i => i.Misc?.InstanceAccountId is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { InstanceAccountId = StringOf(v) }
                }),
            E("CountGameTime", null,
                (_, i) => i.Misc?.CountGameTime,
                i => i.Misc?.CountGameTime is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { CountGameTime = BoolOf(v) }
                }),
            E("linkedInstances", null,
                (_, i) => i.Misc?.LinkedInstances,
                i => i.Misc?.LinkedInstances is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { LinkedInstances = ToStringList(v) }
                }),
            E("GlobalDataPacksEnabled", null,
                (_, i) => i.Misc?.GlobalDataPacksEnabled,
                i => i.Misc?.GlobalDataPacksEnabled is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { GlobalDataPacksEnabled = BoolOf(v) }
                }),
            E("GlobalDataPacksPath", null,
                (_, i) => i.Misc?.GlobalDataPacksPath,
                i => i.Misc?.GlobalDataPacksPath is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { GlobalDataPacksPath = StringOf(v) }
                }),
            E("UseLatestMinecraftVersion", null,
                (_, i) => i.Misc?.UseLatestMinecraftVersion,
                i => i.Misc?.UseLatestMinecraftVersion is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { UseLatestMinecraftVersion = BoolOf(v) }
                }),
            E("UseLatestMinecraftVersionType", null,
                (_, i) => i.Misc?.UseLatestMinecraftVersionType,
                i => i.Misc?.UseLatestMinecraftVersionType is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { UseLatestMinecraftVersionType = StringOf(v) }
                }),
            E("InstanceType", null,
                (_, i) => i.Misc?.InstanceType,
                i => i.Misc?.InstanceType is not null,
                (i, v) => i with
                {
                    Misc = (i.Misc ?? new InstanceMiscConfig()) with { InstanceType = StringOf(v) }
                }),

            // ---- managed pack (the ManagedPack family)
            E("ManagedPack", null,
                (_, i) => i.Managed?.IsManagedPack,
                i => i.Managed?.IsManagedPack is not null,
                (i, v) => i with
                {
                    Managed = (i.Managed ?? new InstanceManagedConfig()) with { IsManagedPack = BoolOf(v) }
                }),
            E("ManagedPackType", null,
                (_, i) => i.Managed?.Type,
                i => i.Managed?.Type is not null,
                (i, v) => i with { Managed = (i.Managed ?? new InstanceManagedConfig()) with { Type = StringOf(v) } }),
            E("ManagedPackID", null,
                (_, i) => i.Managed?.Id,
                i => i.Managed?.Id is not null,
                (i, v) => i with { Managed = (i.Managed ?? new InstanceManagedConfig()) with { Id = StringOf(v) } }),
            E("ManagedPackName", null,
                (_, i) => i.Managed?.Name,
                i => i.Managed?.Name is not null,
                (i, v) => i with { Managed = (i.Managed ?? new InstanceManagedConfig()) with { Name = StringOf(v) } }),
            E("ManagedPackVersionID", null,
                (_, i) => i.Managed?.VersionId,
                i => i.Managed?.VersionId is not null,
                (i, v) => i with
                {
                    Managed = (i.Managed ?? new InstanceManagedConfig()) with { VersionId = StringOf(v) }
                }),
            E("ManagedPackVersionName", null,
                (_, i) => i.Managed?.VersionName,
                i => i.Managed?.VersionName is not null,
                (i, v) => i with
                {
                    Managed = (i.Managed ?? new InstanceManagedConfig()) with { VersionName = StringOf(v) }
                }),
            E("ManagedPackURL", null,
                (_, i) => i.Managed?.Url,
                i => i.Managed?.Url is not null,
                (i, v) => i with { Managed = (i.Managed ?? new InstanceManagedConfig()) with { Url = StringOf(v) } }),

            // ---- export family + selected profiler
            E("ExportName", null,
                (_, i) => i.Export?.Name,
                i => i.Export?.Name is not null,
                (i, v) => i with { Export = (i.Export ?? new InstanceExportConfig()) with { Name = StringOf(v) } }),
            E("ExportVersion", null,
                (_, i) => i.Export?.Version,
                i => i.Export?.Version is not null,
                (i, v) => i with { Export = (i.Export ?? new InstanceExportConfig()) with { Version = StringOf(v) } }),
            E("ExportSummary", null,
                (_, i) => i.Export?.Summary,
                i => i.Export?.Summary is not null,
                (i, v) => i with { Export = (i.Export ?? new InstanceExportConfig()) with { Summary = StringOf(v) } }),
            E("ExportAuthor", null,
                (_, i) => i.Export?.Author,
                i => i.Export?.Author is not null,
                (i, v) => i with { Export = (i.Export ?? new InstanceExportConfig()) with { Author = StringOf(v) } }),
            E("ExportOptionalFiles", null,
                (_, i) => i.Export?.OptionalFiles,
                i => i.Export?.OptionalFiles is not null,
                (i, v) => i with
                {
                    Export = (i.Export ?? new InstanceExportConfig()) with { OptionalFiles = BoolOf(v) }
                }),
            E("ExportRecommendedRAM", null,
                (_, i) => i.Export?.RecommendedRam,
                i => i.Export?.RecommendedRam is not null,
                (i, v) => i with
                {
                    Export = (i.Export ?? new InstanceExportConfig()) with
                    {
                        // IntOf already preserves null ("not configured", the C++ default)
                        RecommendedRam = IntOf(v)
                    }
                }),
            E("Profiler", null,
                (_, i) => i.Export?.Profiler,
                i => i.Export?.Profiler is not null,
                (i, v) => i with { Export = (i.Export ?? new InstanceExportConfig()) with { Profiler = StringOf(v) } }),

            // ---- the C7 passthrough pair: global-only reads, instance writes dropped
            E("ConsoleMaxLines", null,
                (u, _) => u.Console!.ConsoleMaxLines,
                _ => false,
                (i, _) => i),
            E("ConsoleOverflowStop", null,
                (u, _) => u.Console!.ConsoleOverflowStop,
                _ => false,
                (i, _) => i),

            // ---- the derived gates (C7: read = any family member non-null; write false = clear)
            E("OverrideJavaLocation", "OverrideJavaLocation", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateJavaLocation(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateJavaLocation, ClearJavaLocation)),
            E("OverrideJavaArgs", "OverrideJavaArgs", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateJavaArgs(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateJavaArgs, ClearJavaArgs)),
            E("OverrideMemory", "OverrideMemory", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateMemory(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateMemory, ClearMemory)),
            E("OverrideWindow", "OverrideWindow", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateWindow(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateWindow, ClearWindow)),
            E("OverrideCommands", "OverrideCommands", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateCommands(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateCommands, ClearCommands)),
            E("OverrideLaunchCmd", "OverrideCommands", // the legacy synonym of the commands gate
                (_, i) => GateCommands(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateCommands, ClearCommands)),
            E("OverrideEnv", "OverrideEnv", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateEnv(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateEnv, ClearEnv)),
            E("OverrideConsole", "OverrideConsole", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateConsole(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateConsole, ClearConsole)),
            E("OverrideGameTime", "OverrideGameTime", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateGameTime(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateGameTime, ClearGameTime)),
            E("OverrideNativeWorkarounds", "OverrideNativeWorkarounds", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateNativeLib(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateNativeLib, ClearNativeLib)),
            E("OverridePerformance", "OverridePerformance", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GatePerformance(i),
                _ => false,
                (i, v) => GateWrite(i, v, GatePerformance, ClearPerformance)),
            E("OverrideMiscellaneous", "OverrideMiscellaneous", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateMiscellaneous(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateMiscellaneous, ClearMiscellaneous)),
            E("OverrideLegacySettings", "OverrideLegacySettings", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateLegacySettings(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateLegacySettings, ClearLegacySettings)),
            E("OverrideModDownloadLoaders", "OverrideModDownloadLoaders", // the family this derived gate belongs to (its INI spelling lives in GateIniSpellings)
                (_, i) => GateModDownloadLoaders(i),
                _ => false,
                (i, v) => GateWrite(i, v, GateModDownloadLoaders, ClearModDownloadLoaders))
        ];

        var map = new Dictionary<string, InstanceKeyEntry>(StringComparer.Ordinal);
        foreach (var entry in entries) map.Add(entry.Key, entry);
        return map;
    }

    /// <summary>
    ///     linkedInstances accepts a real string list (typed writes) or the legacy JSON
    ///     array text (INI import). Null passes through (reset to inherit). Entries are stored
    ///     verbatim — the dirname→uuid conversion (D3) is the discovery-side pass in
    ///     InstanceList, not a transform of this layer.
    /// </summary>
    private static IReadOnlyList<string>? ToStringList(object? value) => value switch
    {
        null => null,
        IReadOnlyList<string> list => list,
        IEnumerable<string> en => en.ToList(),
        string text => JsonUtils.ToStringList(text),
        _ => null
    };
}
