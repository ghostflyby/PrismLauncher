// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Migration;

/// <summary>One row of the projection table: the INI key (primary spelling plus the C++
/// synonym spellings) and the flint.json member it lands on when that member is absent.
/// The member name is its own column — the INI spellings and the record members diverge
/// (UseNativeOpenAL vs UseNativeOpenAl, PastebinCustomAPIBase vs PastebinCustomApi).</summary>
internal sealed record LegacyConfigKey(string Primary, string[] Aliases, string Domain, string Member);

/// <summary>The legacy INI → flint.json projection.</summary>
public static class LegacyConfigProjection
{
    /// <summary>The secret trio: the INI names and synonyms whose home is the secret store,
    /// never the record. A stored secret is never overwritten (the same merge rule).</summary>
    public static readonly IReadOnlyList<(string Primary, string[] Aliases)> SecretKeys =
    [
        ("ProxyPass", ["ProxyPassword"]),
        ("FlameKeyOverride", ["CFKeyOverride"]),
        ("ModrinthToken", []),
    ];

    /// <summary>The record-domain rows. Grouped exactly like the C++ registerSetting sections;
    /// the domain grouping is not derivable from the key names.</summary>
    internal static readonly IReadOnlyList<LegacyConfigKey> RecordKeys =
    [
        // ---- launcher
        new("MenuBarInsteadOfToolBar", [], "Launcher", "MenuBarInsteadOfToolBar"),
        new("NumberOfConcurrentTasks", [], "Launcher", "NumberOfConcurrentTasks"),
        new("NumberOfConcurrentDownloads", [], "Launcher", "NumberOfConcurrentDownloads"),
        new("NumberOfManualRetries", [], "Launcher", "NumberOfManualRetries"),
        new("RequestTimeout", [], "Launcher", "RequestTimeout"),
        new("CloseAfterLaunch", [], "Launcher", "CloseAfterLaunch"),
        new("QuitAfterGameStop", [], "Launcher", "QuitAfterGameStop"),
        new("InstSortMode", [], "Launcher", "InstSortMode"),
        new("InstRenamingMode", [], "Launcher", "InstRenamingMode"),
        // ---- console
        new("ShowConsole", [], "Console", "ShowConsole"),
        new("AutoCloseConsole", [], "Console", "AutoCloseConsole"),
        new("ShowConsoleOnError", [], "Console", "ShowConsoleOnError"),
        new("LogPrePostOutput", [], "Console", "LogPrePostOutput"),
        new("ConsoleMaxLines", [], "Console", "ConsoleMaxLines"),
        new("ConsoleOverflowStop", [], "Console", "ConsoleOverflowStop"),
        // ---- game time
        new("ShowGameTime", [], "GameTime", "ShowGameTime"),
        new("ShowGlobalGameTime", [], "GameTime", "ShowGlobalGameTime"),
        new("ShowGameTimeWithoutDays", [], "GameTime", "ShowGameTimeWithoutDays"),
        new("RecordGameTime", [], "GameTime", "RecordGameTime"),
        // ---- window
        new("LaunchMaximized", ["MCWindowMaximize"], "Window", "LaunchMaximized"),
        new("MinecraftWinWidth", ["MCWindowWidth"], "Window", "MinecraftWinWidth"),
        new("MinecraftWinHeight", ["MCWindowHeight"], "Window", "MinecraftWinHeight"),
        // ---- memory
        new("MinMemAlloc", ["MinMemoryAlloc"], "Memory", "MinMemAlloc"),
        new("MaxMemAlloc", ["MaxMemoryAlloc"], "Memory", "MaxMemAlloc"),
        new("LowMemWarning", [], "Memory", "LowMemWarning"),
        // ---- performance
        new("EnableFeralGamemode", [], "Performance", "EnableFeralGamemode"),
        new("EnableMangoHud", [], "Performance", "EnableMangoHud"),
        new("UseDiscreteGpu", [], "Performance", "UseDiscreteGpu"),
        new("UseZink", [], "Performance", "UseZink"),
        // ---- native library workarounds
        new("UseNativeOpenAL", [], "NativeLib", "UseNativeOpenAl"),
        new("CustomOpenALPath", [], "NativeLib", "CustomOpenAlPath"),
        new("UseNativeGLFW", [], "NativeLib", "UseNativeGlfw"),
        new("CustomGLFWPath", [], "NativeLib", "CustomGlfwPath"),
        new("UseNativeSDL", [], "NativeLib", "UseNativeSdl"),
        new("CustomSDLPath", [], "NativeLib", "CustomSdlPath"),
        // ---- commands
        new("PreLoadCommand", ["PreLoadCmd"], "Commands", "PreLoadCommand"),
        new("PreLaunchCommand", ["PreLaunchCmd"], "Commands", "PreLaunchCommand"),
        new("WrapperCommand", [], "Commands", "WrapperCommand"),
        new("PostExitCommand", ["PostExitCmd"], "Commands", "PostExitCommand"),
        // ---- minecraft misc
        new("OnlineFixes", [], "MinecraftMisc", "OnlineFixes"),
        new("Env", [], "MinecraftMisc", "Env"),
        // ---- mod download
        new("ModMetadataDisabled", [], "ModDownload", "ModMetadataDisabled"),
        new("ModDependenciesDisabled", [], "ModDownload", "ModDependenciesDisabled"),
        new("SkipModpackUpdatePrompt", [], "ModDownload", "SkipModpackUpdatePrompt"),
        new("ShowModIncompat", [], "ModDownload", "ShowModIncompat"),
        new("DownloadGameFilesDuringInstanceCreation", [], "ModDownload", "DownloadGameFilesDuringInstanceCreation"),
        new("ModUpdateReleaseTypes", [], "ModDownload", "ModUpdateReleaseTypes"),
        // ---- java
        new("JavaPath", [], "Java", "JavaPath"),
        new("JvmArgs", [], "Java", "JvmArgs"),
        new("IgnoreJavaCompatibility", [], "Java", "IgnoreJavaCompatibility"),
        new("IgnoreJavaWizard", [], "Java", "IgnoreJavaWizard"),
        new("AutomaticJavaSwitch", [], "Java", "AutomaticJavaSwitch"),
        new("AutomaticJavaDownload", [], "Java", "AutomaticJavaDownload"),
        new("AutomaticJava", [], "Java", "AutomaticJava"),
        // ---- dirs
        new("IconsDir", [], "Dirs", "IconsDir"),
        new("SkinsDir", [], "Dirs", "SkinsDir"),
        new("JavaDir", [], "Dirs", "JavaDir"),
        new("DownloadsDir", [], "Dirs", "DownloadsDir"),
        new("DownloadsDirWatchRecursive", [], "Dirs", "DownloadsDirWatchRecursive"),
        new("MoveModsFromDownloadsDir", [], "Dirs", "MoveModsFromDownloadsDir"),
        // ---- language
        new("Language", [], "Language", "Language"),
        new("UseSystemLocale", [], "Language", "UseSystemLocale"),
        // ---- mirrors
        new("MetaURLOverride", [], "Mirror", "MetaUrlOverride"),
        new("ResourceURLOverride", ["ResourceURL"], "Mirror", "ResourceUrlOverride"),
        new("LegacyFMLLibsURLOverride", [], "Mirror", "LegacyFmlLibsUrlOverride"),
        // ---- proxy (no Pass row — the password is a secret above)
        new("ProxyType", [], "Proxy", "ProxyType"),
        new("ProxyAddr", ["ProxyHostName"], "Proxy", "ProxyAddr"),
        new("ProxyPort", [], "Proxy", "ProxyPort"),
        new("ProxyUser", ["ProxyUsername"], "Proxy", "ProxyUser"),
        // ---- api (no credential rows — the pair are secrets above)
        new("MSAClientIDOverride", [], "Api", "MsaClientIdOverride"),
        new("UserAgentOverride", [], "Api", "UserAgentOverride"),
        new("PastebinType", [], "Api", "PastebinType"),
        new("PastebinCustomAPIBase", [], "Api", "PastebinCustomApiBase"),
        // ---- tools
        new("JsonEditor", [], "Tools", "JsonEditor"),
        new("JProfilerPath", [], "Tools", "JProfilerPath"),
        new("JVisualVMPath", [], "Tools", "JVisualVmPath"),
        new("MCEditPath", [], "Tools", "McEditPath"),
    ];

    /// <summary>Project the INI onto the raw payload: every row whose key the file carries
    /// lands on its member WHEN that member is still at its record default. Returns the
    /// projected payload (the same instance when nothing applied).</summary>
    public static FlintConfigFile Project(FlintConfigFile raw, IniFile ini)
    {
        foreach (var key in RecordKeys)
        {
            if (Find(ini, key.Primary, key.Aliases) is not { } value)
            {
                continue;
            }

            if (!AtDefault(raw, key.Domain, key.Member))
            {
                continue; // the merge rule: a member the user customized wins over the INI
            }

            if (Coerce(value, DomainMemberType(key.Domain, key.Member)) is not { } coerced)
            {
                continue; // a null coercion stays unset (an unparseable Env yields the empty
                          // map, which equals the default — a no-op fill, not a null)
            }

            raw = WithMember(raw, key.Domain, key.Member, coerced);
        }

        return raw;
    }

    // flint.json stores the RESOLVED shape (every member materialized), so "absent" is not
    // observable — the closest faithful merge signal is "still at the record default": a
    // member the user never customized takes the INI value, a customized one keeps it
    private static readonly FlintConfigFile Defaults = FlintConfigFile.Default();

    private static bool AtDefault(FlintConfigFile raw, string domain, string member)
    {
        var current = GetMember(raw, domain, member);
        var defaultValue = GetMember(Defaults, domain, member);
        return current is EnvMap map && defaultValue is EnvMap defaultMap
            ? map.OrderBy(entry => entry.Key).SequenceEqual(defaultMap.OrderBy(entry => entry.Key))
            : Equals(current, defaultValue);
    }

    /// <summary>The INI value for one key: the primary spelling first (when the file carries
    /// both spellings the primary wins regardless of file order — the C++ retrieveValue walks
    /// configKeys() primary-first), then the synonyms in declaration order.</summary>
    public static IniValue? Find(IniFile ini, string primary, IReadOnlyList<string> aliases)
    {
        if (Present(ini, primary) is { } value)
        {
            return value;
        }

        foreach (var alias in aliases)
        {
            if (Present(ini, alias) is { } aliasValue)
            {
                return aliasValue;
            }
        }

        return null;
    }

    private static IniValue? Present(IniFile ini, string key)
    {
        if (ini.TryGetValue(key, out var value) && value.ValueKind != IniValue.Kind.Invalid)
        {
            return value;
        }

        return null;
    }

    /// <summary>The QVariant-compatible coercion for one target type. The closed set is the
    /// config scalars; anything else is a table bug and fails loudly at first use.</summary>
    private static object? Coerce(IniValue value, Type target)
    {
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        var boxed = Variant.FromIniValue(value);
        return underlying == typeof(EnvMap)
            ? EnvMapJsonConverter.FromObject(boxed)
            : underlying == typeof(bool)
                ? Variant.AsBool(boxed)
                : underlying == typeof(int)
                    ? Variant.AsInt(boxed)
                    : underlying == typeof(long)
                        ? Variant.AsLong(boxed)
                        : underlying == typeof(string)
                            ? Variant.AsString(boxed)
                            : throw new NotSupportedException($"no legacy coercion for {underlying.Name}");
    }

    // ------------------------------------------------------------------ the record lens

    private const DynamicallyAccessedMemberTypes MemberFlags =
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors
                                                        | DynamicallyAccessedMemberTypes.PublicMethods;

    // the closed record set: domain name → annotated type. A switch over typeof(...) keeps
    // every type statically known (PropertyInfo.PropertyType is not annotated — the trimmer
    // could not vouch for members reached through it).
    [return: DynamicallyAccessedMembers(MemberFlags)]
    private static Type DomainType(string domain) => domain.ToLowerInvariant() switch
    {
        "launcher" => typeof(LauncherConfig),
        "console" => typeof(ConsoleConfig),
        "gametime" => typeof(GameTimeConfig),
        "window" => typeof(WindowConfig),
        "memory" => typeof(MemoryConfig),
        "performance" => typeof(PerformanceConfig),
        "nativelib" => typeof(NativeLibConfig),
        "commands" => typeof(CommandsConfig),
        "minecraftmisc" => typeof(MinecraftMiscConfig),
        "moddownload" => typeof(ModDownloadConfig),
        "java" => typeof(JavaConfig),
        "dirs" => typeof(DirsConfig),
        "language" => typeof(LanguageConfig),
        "mirror" => typeof(MirrorConfig),
        "proxy" => typeof(ProxyConfig),
        "api" => typeof(ApiConfig),
        "tools" => typeof(ToolsConfig),
        _ => throw new ArgumentException($"unknown config domain '{domain}'", nameof(domain)),
    };

    private static Type DomainMemberType(string domain, string member) =>
        DomainType(domain).GetProperty(member)?.PropertyType
        ?? throw new ArgumentException($"unknown member '{member}' of domain '{domain}'");

    private static object? GetMember(FlintConfigFile raw, string domain, string member)
    {
        var domainObject = DomainProperty(domain).GetValue(raw);
        return DomainType(domain).GetProperty(member)?.GetValue(domainObject);
    }

    /// <summary>Clone the root with one domain member replaced. The root has no parameterless
    /// constructor — its single public constructor takes every domain by name, so the clone
    /// re-invokes it with the existing domains and the rebuilt one.</summary>
    private static FlintConfigFile WithMember(FlintConfigFile raw, string domain, string member, object? value)
    {
        var domainProperty = DomainProperty(domain);
        var rebuilt = CloneWithMember(DomainType(domain), domainProperty.GetValue(raw)!, member, value);
        var constructor = typeof(FlintConfigFile).GetConstructors().Single();
        var arguments = new object?[constructor.GetParameters().Length];
        var index = 0;
        foreach (var parameter in constructor.GetParameters())
        {
            // the constructor parameters are the camelCase domain names; the lookups stay
            // exact-name (the trimmer vouches for exactly-named members), the case folding
            // is the comparison's
            arguments[index++] = string.Equals(parameter.Name, domain, StringComparison.OrdinalIgnoreCase)
                ? rebuilt
                : RootProperty(parameter.Name!)?.GetValue(raw);
        }

        return (FlintConfigFile)constructor.Invoke(arguments)!;
    }

    private static PropertyInfo DomainProperty(string domain) =>
        typeof(FlintConfigFile).GetProperty(domain)
        ?? throw new ArgumentException($"unknown config domain '{domain}'", nameof(domain));

    private static PropertyInfo? RootProperty(string property) =>
        typeof(FlintConfigFile).GetProperties().FirstOrDefault(candidate => string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase));

    /// <summary>Clone a domain record with one member replaced: a fresh instance (the domain
    /// records are parameterless) plus every property copied, the target member set. The
    /// setters are init-only — reflection hides them at the PropertyInfo level entirely
    /// (GetSetMethod yields null, SetValue refuses), so the clone invokes the underlying
    /// set_METHOD by name: the path the compiler bars outside initializers.</summary>
    private static object CloneWithMember(
        [DynamicallyAccessedMembers(MemberFlags)]
        Type domainType, object domain, string member, object? value)
    {
        var clone = Activator.CreateInstance(domainType)!;
        // instance properties only: the domain records also carry STATIC get-only helpers
        // (DefaultMaxMemAlloc, DefaultDownloadsDir) that have no setter to invoke
        foreach (var property in domainType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var argument = string.Equals(property.Name, member, StringComparison.OrdinalIgnoreCase)
                ? value
                : property.GetValue(domain);
            domainType.GetMethod("set_" + property.Name)!.Invoke(clone, [argument]);
        }

        return clone;
    }
}
