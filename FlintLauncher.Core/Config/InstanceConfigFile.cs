// SPDX-License-Identifier: GPL-3.0-only
// The instance.json record domain (DATA D4 + CONFIG C5/C7, ask shape: Identity / Launch /
// Managed / Export / Misc — "拆分形状和核心配置一样"). One file per instance directory next
// to the untouched mmc-pack.json + patches/ (read-compat, C5) and the machine-local
// instance-state.json (the runtime time trio lives there, not here).
//
// Layering shape (C6/C7): every MEMBER is NULLABLE — null means "not set at this layer,
// inherit from the user layer (flint.json) below". The gate booleans of the old instance.cfg
// (OverrideMemory & co) DEGENERATE to "is any member of the domain non-null" (CONFIG C7 §D);
// the reader is <see cref="InstanceConfigKeys"/>, which resolves each key as
// instance-layer ?? user-layer and derives the gates. There are no property defaults on
// layered members on purpose: the user layer (flint.json's record defaults) is the single
// default source. Instance-ONLY keys (Identity, JoinServer*, Export, …) have no lower layer;
// their defaults stay where the C++ RegisterSetting calls had them — the Setting
// registrations in BaseInstance/MinecraftInstance (Setting.Get falls back to them).
//
// uuid belongs to Identity (D4: "身份不是状态") and is the D2 addressing chain's persistent
// handle; the INI import carries a Prism instance.cfg's uuid over VERBATIM so references
// survive the format switch.
//
// All types are registered into FlintJsonContext (AOT red line: a missing root registration
// is a runtime failure, not a compile error).
namespace FlintLauncher.Core.Config;

/// <summary>The instance.json root (D4). One object with a camelCase member per domain;
/// absent/null members inherit from the user layer or the registered defaults.</summary>
public sealed record InstanceConfigFile
{
    /// <summary>The fresh-instance shape: empty domains, every member unset.</summary>
    public static InstanceConfigFile Default() => new();

    public InstanceIdentityConfig Identity { get; init; } = new();
    public InstanceLaunchConfig Launch { get; init; } = new();
    public InstanceManagedConfig Managed { get; init; } = new();
    public InstanceExportConfig Export { get; init; } = new();
    public InstanceMiscConfig Misc { get; init; } = new();
}

/// <summary>Who this instance is (D4: uuid is identity, not state). Name/iconKey/notes
/// defaults live in the BaseInstance Setting registrations ("Unnamed Instance"/"default"/"").</summary>
public sealed record InstanceIdentityConfig
{
    public string? Uuid { get; init; }
    public string? Name { get; init; }
    public string? IconKey { get; init; }
    public string? Notes { get; init; }

    /// <summary>The registered shortcuts array as its compact JSON text (the C++ stored the
    /// QJsonDocument payload verbatim; kept opaque — the shape is BaseInstance's domain).</summary>
    public string? Shortcuts { get; init; }
}

/// <summary>The layered launch settings (the C7 override families): java / memory / window /
/// commands / env / performance / nativeLib.</summary>
public sealed record InstanceLaunchConfig
{
    public InstanceJavaConfig Java { get; init; } = new();
    public InstanceMemoryConfig Memory { get; init; } = new();
    public InstanceWindowConfig Window { get; init; } = new();
    public InstanceCommandsConfig Commands { get; init; } = new();

    /// <summary>The custom environment map as its JSON object text (same shape as the C++
    /// "Env" value; gated by OverrideEnv on import).</summary>
    public string? Env { get; init; }

    public InstancePerformanceConfig Performance { get; init; } = new();
    public InstanceNativeLibConfig NativeLib { get; init; } = new();
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
    public int? PermGen { get; init; }
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
    public bool? UseNativeOpenAL { get; init; }
    public string? CustomOpenALPath { get; init; }
    public bool? UseNativeGLFW { get; init; }
    public string? CustomGLFWPath { get; init; }
    public bool? UseNativeSDL { get; init; }
    public string? CustomSDLPath { get; init; }
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
    // ---- layered override families (null member = inherit)

    public InstanceConsoleConfig? Console { get; init; }
    public InstanceGameTimeConfig? GameTime { get; init; }

    /// <summary>OverrideMiscellaneous family.</summary>
    public bool? CloseAfterLaunch { get; init; }
    public bool? QuitAfterGameStop { get; init; }

    /// <summary>OverrideLegacySettings family.</summary>
    public bool? OnlineFixes { get; init; }

    /// <summary>OverrideModDownloadLoaders family: the loader list as its JSON array text.</summary>
    public string? ModDownloadLoaders { get; init; }

    // ---- instance-only keys (no global layer)

    public bool? JoinServerOnLaunch { get; init; }
    public string? JoinServerOnLaunchAddress { get; init; }
    public string? JoinWorldOnLaunch { get; init; }
    public bool? UseAccountForInstance { get; init; }
    public string? InstanceAccountId { get; init; }

    /// <summary>Count this instance's playtime into the launcher-state global total.</summary>
    public bool? CountGameTime { get; init; }

    /// <summary>D3: uuid references to linked instances (dirname references are converted at
    /// import; the C++ stored the same list as a JSON string — here it is a real array).</summary>
    public IReadOnlyList<string>? LinkedInstances { get; init; }

    public bool? GlobalDataPacksEnabled { get; init; }

    /// <summary>CONFIG matrix C: machine-local path (not synced), like the C++.</summary>
    public string? GlobalDataPacksPath { get; init; }

    public bool? UseLatestMinecraftVersion { get; init; }
    public string? UseLatestMinecraftVersionType { get; init; }

    /// <summary>The format discriminator ("OneSix" is the only instantiable type; unknown
    /// types load as NullInstance). Defaults to OneSix through its Setting registration.</summary>
    public string? InstanceType { get; init; }
}

/// <summary>Console visibility family (OverrideConsole). ConsoleMaxLines/ConsoleOverflowStop
/// are NOT here — C7 passthrough keys stay global-only.</summary>
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
