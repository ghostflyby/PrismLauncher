// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The instance.json record domain — Identity / Launch / Managed / Export / Misc — the
// same domain split as the core config. One file per instance directory next
// to the untouched mmc-pack.json + patches/ (read-compat) and the machine-local
// instance-state.json (the runtime time trio lives there, not here).
//
// Layering shape: every MEMBER is NULLABLE — null means "not set at this layer,
// inherit from the user layer (flint.json) below". The gate booleans of the old instance.cfg
// (OverrideMemory & co) DEGENERATE to "is any member of the domain non-null";
// the reader is <see cref="InstanceConfigKeys"/>, which resolves each key as
// instance-layer ?? user-layer and derives the gates. There are no property defaults on
// layered members on purpose: the user layer (flint.json's record defaults) is the single
// default source. Instance-ONLY keys (Identity, JoinServer*, Export, …) have no lower layer;
// their defaults stay where the C++ RegisterSetting calls had them — the Setting
// registrations in BaseInstance/MinecraftInstance (Setting.Get falls back to them).
//
// uuid belongs to Identity (identity is not state) and is the addressing chain's persistent
// handle; the INI import carries a Prism instance.cfg's uuid over VERBATIM so references
// survive the format switch.
//
// All types are registered into FlintJsonContext (AOT red line: a missing root registration
// is a runtime failure, not a compile error).

using System.Text.Json.Serialization;
using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Config;

/// <summary>The instance.json root. One object with a camelCase member per domain;
/// absent scalar members inherit from the user layer or the registered defaults.</summary>
public sealed record InstanceConfigFile
{
    /// <summary>The fresh-instance shape: every scalar unset, every domain constructed.</summary>
    public static InstanceConfigFile Default() => new();

    // The materializing constructor: the source-generated deserializer overwrites init-only
    // property initializers with null for members absent from the payload (.NET 10), so the
    // constructor body re-materializes the domains — every in-memory InstanceConfigFile has
    // real domain values regardless of the on-disk shape. The layer semantics live at the
    // SCALAR level (null member = inherit from the user layer / registered defaults),
    // resolved by <see cref="InstanceConfigKeys"/>.
    [JsonConstructor]
    public InstanceConfigFile(InstanceIdentityConfig? identity = null,
        InstanceLaunchConfig? launch = null, InstanceManagedConfig? managed = null,
        InstanceExportConfig? export = null, InstanceMiscConfig? misc = null)
    {
        Identity = identity ?? new InstanceIdentityConfig();
        Launch = launch ?? new InstanceLaunchConfig();
        Managed = managed ?? new InstanceManagedConfig();
        Export = export ?? new InstanceExportConfig();
        Misc = misc ?? new InstanceMiscConfig();
    }

    public InstanceIdentityConfig Identity { get; init; }
    public InstanceLaunchConfig Launch { get; init; }
    public InstanceManagedConfig Managed { get; init; }
    public InstanceExportConfig Export { get; init; }
    public InstanceMiscConfig Misc { get; init; }
}

/// <summary>Who this instance is (uuid is identity, not state). Name/iconKey/notes
/// defaults live in the BaseInstance Setting registrations ("Unnamed Instance"/"default"/"").</summary>
public sealed record InstanceIdentityConfig
{
    public string? Uuid { get; init; }
    public string? Name { get; init; }
    public string? IconKey { get; init; }
    public string? Notes { get; init; }

    /// <summary>The registered shortcuts (the C++ stored the QJsonDocument payload as an
    /// opaque string; read compat accepts that legacy form, writes are the native array —
    /// the launcher config write shape is not guaranteed).</summary>
    [JsonConverter(typeof(ShortcutListJsonConverter))]
    public List<ShortcutData>? Shortcuts { get; init; }
}

/// <summary>The layered launch settings (the override families): java / memory / window /
/// commands / env / performance / nativeLib.</summary>
public sealed record InstanceLaunchConfig
{
    // The materializing constructor — same reasoning as the InstanceConfigFile root: a
    // partial payload like {"launch":{"memory":{"minMemAlloc":…}}} would otherwise
    // deserialize the absent families as null.
    [JsonConstructor]
    public InstanceLaunchConfig(InstanceJavaConfig? java = null, InstanceMemoryConfig? memory = null,
        InstanceWindowConfig? window = null, InstanceCommandsConfig? commands = null,
        EnvMap? env = null, InstancePerformanceConfig? performance = null,
        InstanceNativeLibConfig? nativeLib = null)
    {
        Java = java ?? new InstanceJavaConfig();
        Memory = memory ?? new InstanceMemoryConfig();
        Window = window ?? new InstanceWindowConfig();
        Commands = commands ?? new InstanceCommandsConfig();
        Env = env;
        Performance = performance ?? new InstancePerformanceConfig();
        NativeLib = nativeLib ?? new InstanceNativeLibConfig();
    }

    public InstanceJavaConfig Java { get; init; }
    public InstanceMemoryConfig Memory { get; init; }
    public InstanceWindowConfig Window { get; init; }
    public InstanceCommandsConfig Commands { get; init; }

    /// <summary>The custom environment map (the C++ "Env" value; gated by OverrideEnv on
    /// import). Read compat accepts the legacy embedded-JSON string form; writes are the
    /// native object — the launcher config write shape is not guaranteed.</summary>
    [JsonConverter(typeof(EnvMapJsonConverter))]
    public EnvMap? Env { get; init; }

    public InstancePerformanceConfig Performance { get; init; }
    public InstanceNativeLibConfig NativeLib { get; init; }
}

/// <summary>Java location/arguments (the OverrideJavaLocation family: JavaPath +
/// IgnoreJavaCompatibility; OverrideJavaArgs: JvmArgs) and the instance-side automatic-java
/// flag (no global gate — instance-only, default false via its Setting registration).</summary>
public sealed record InstanceJavaConfig
{
    public string? JavaPath { get; init; }
    public string? JvmArgs { get; init; }
    public bool? IgnoreJavaCompatibility { get; init; }
    public bool? AutomaticJava { get; init; }
}

/// <summary>JVM memory allocation (OverrideMemory family).</summary>
public sealed record InstanceMemoryConfig
{
    public int? MinMemAlloc { get; init; }

    public int? MaxMemAlloc { get; init; }

    // PermGen died with the Java <8 support floor (2026-10-05): PermSize is a pre-8 concept.
    public bool? LowMemWarning { get; init; }
}

/// <summary>Launch window shape (OverrideWindow family).</summary>
public sealed record InstanceWindowConfig
{
    public bool? LaunchMaximized { get; init; }
    public int? MinecraftWinWidth { get; init; }
    public int? MinecraftWinHeight { get; init; }
}

/// <summary>Custom commands around the game process (OverrideCommands, synonym
/// OverrideLaunchCmd).</summary>
public sealed record InstanceCommandsConfig
{
    public string? PreLoadCommand { get; init; }
    public string? PreLaunchCommand { get; init; }
    public string? WrapperCommand { get; init; }
    public string? PostExitCommand { get; init; }
}

/// <summary>Linux performance wrappers (OverridePerformance family).</summary>
public sealed record InstancePerformanceConfig
{
    public bool? EnableFeralGamemode { get; init; }
    public bool? EnableMangoHud { get; init; }
    public bool? UseDiscreteGpu { get; init; }
    public bool? UseZink { get; init; }
}

/// <summary>Native library workarounds (OverrideNativeWorkarounds family).</summary>
public sealed record InstanceNativeLibConfig
{
    public bool? UseNativeOpenAl { get; init; }
    public string? CustomOpenAlPath { get; init; }
    public bool? UseNativeGlfw { get; init; }
    public string? CustomGlfwPath { get; init; }
    public bool? UseNativeSdl { get; init; }
    public string? CustomSdlPath { get; init; }
}

/// <summary>Managed-pack adoption metadata (the ManagedPack family; written by the pack
/// install tasks and the managed-pack update flow).</summary>
public sealed record InstanceManagedConfig
{
    public bool? IsManagedPack { get; init; }
    public string? Type { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? VersionId { get; init; }
    public string? VersionName { get; init; }
    public string? Url { get; init; }
}

/// <summary>Modpack-export metadata (the Export family) plus the selected profiler — all
/// instance-only keys (no global layer).</summary>
public sealed record InstanceExportConfig
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Summary { get; init; }
    public string? Author { get; init; }
    public bool? OptionalFiles { get; init; }

    /// <summary>Unset = "not configured" (the C++ registered it without a default).</summary>
    public int? RecommendedRam { get; init; }

    public string? Profiler { get; init; }
}

/// <summary>Everything instance-layer that is neither Identity nor one of the Launch
/// families: the console/game-time override families, the miscellaneous gate pair
/// (CloseAfterLaunch/QuitAfterGameStop), OnlineFixes (OverrideLegacySettings), the mod
/// loader list (OverrideModDownloadLoaders) and the instance-only keys.</summary>
public sealed record InstanceMiscConfig
{
    // The materializing constructor — Console/GameTime are re-created when the payload
    // omits them; the scalar members pass through (null = inherit / unset).
    [JsonConstructor]
    public InstanceMiscConfig(InstanceConsoleConfig? console = null,
        InstanceGameTimeConfig? gameTime = null, bool? closeAfterLaunch = null,
        bool? quitAfterGameStop = null, bool? onlineFixes = null,
        List<string>? modDownloadLoaders = null, bool? joinServerOnLaunch = null,
        string? joinServerOnLaunchAddress = null, string? joinWorldOnLaunch = null,
        bool? useAccountForInstance = null, string? instanceAccountId = null,
        bool? countGameTime = null, IReadOnlyList<string>? linkedInstances = null,
        bool? globalDataPacksEnabled = null, string? globalDataPacksPath = null,
        bool? useLatestMinecraftVersion = null, string? useLatestMinecraftVersionType = null,
        string? instanceType = null)
    {
        Console = console ?? new InstanceConsoleConfig();
        GameTime = gameTime ?? new InstanceGameTimeConfig();
        CloseAfterLaunch = closeAfterLaunch;
        QuitAfterGameStop = quitAfterGameStop;
        OnlineFixes = onlineFixes;
        ModDownloadLoaders = modDownloadLoaders;
        JoinServerOnLaunch = joinServerOnLaunch;
        JoinServerOnLaunchAddress = joinServerOnLaunchAddress;
        JoinWorldOnLaunch = joinWorldOnLaunch;
        UseAccountForInstance = useAccountForInstance;
        InstanceAccountId = instanceAccountId;
        CountGameTime = countGameTime;
        LinkedInstances = linkedInstances;
        GlobalDataPacksEnabled = globalDataPacksEnabled;
        GlobalDataPacksPath = globalDataPacksPath;
        UseLatestMinecraftVersion = useLatestMinecraftVersion;
        UseLatestMinecraftVersionType = useLatestMinecraftVersionType;
        InstanceType = instanceType;
    }

    // ---- layered override families (null member = inherit)

    public InstanceConsoleConfig Console { get; init; }
    public InstanceGameTimeConfig GameTime { get; init; }

    /// <summary>OverrideMiscellaneous family.</summary>
    public bool? CloseAfterLaunch { get; init; }

    public bool? QuitAfterGameStop { get; init; }

    /// <summary>OverrideLegacySettings family.</summary>
    public bool? OnlineFixes { get; init; }

    /// <summary>OverrideModDownloadLoaders family: the loader list. Read compat accepts the
    /// legacy embedded-JSON string form; writes are the native array.</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string>? ModDownloadLoaders { get; init; }

    // ---- instance-only keys (no global layer)

    public bool? JoinServerOnLaunch { get; init; }
    public string? JoinServerOnLaunchAddress { get; init; }
    public string? JoinWorldOnLaunch { get; init; }
    public bool? UseAccountForInstance { get; init; }
    public string? InstanceAccountId { get; init; }

    /// <summary>Count this instance's playtime into the launcher-state global total.</summary>
    public bool? CountGameTime { get; init; }

    /// <summary>The persisted reference form is the instance uuid. This layer stores the
    /// list VERBATIM — the INI import lands whatever the Prism cfg carried (dirnames), and
    /// the conversion to uuids is the discovery-side pass (InstanceList
    /// ConvertLinkedInstanceReferences after each LoadList; unresolvable entries are retried
    /// the next round, like the group-file members). The C++ stored the same list as a JSON
    /// string — here it is a real array.</summary>
    public IReadOnlyList<string>? LinkedInstances { get; init; }

    public bool? GlobalDataPacksEnabled { get; init; }

    /// <summary>Machine-local path (not synced), like the C++.</summary>
    public string? GlobalDataPacksPath { get; init; }

    public bool? UseLatestMinecraftVersion { get; init; }
    public string? UseLatestMinecraftVersionType { get; init; }

    /// <summary>The format discriminator ("OneSix" is the only instantiable type; unknown
    /// types load as NullInstance). Defaults to OneSix through its Setting registration.</summary>
    public string? InstanceType { get; init; }
}

/// <summary>Console visibility family (OverrideConsole). ConsoleMaxLines/ConsoleOverflowStop
/// are NOT here — the passthrough keys stay global-only.</summary>
public sealed record InstanceConsoleConfig
{
    public bool? ShowConsole { get; init; }
    public bool? AutoCloseConsole { get; init; }
    public bool? ShowConsoleOnError { get; init; }
    public bool? LogPrePostOutput { get; init; }
}

/// <summary>Game-time display/recording family (OverrideGameTime).</summary>
public sealed record InstanceGameTimeConfig
{
    public bool? ShowGameTime { get; init; }
    public bool? RecordGameTime { get; init; }
}
