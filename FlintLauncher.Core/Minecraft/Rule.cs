// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/Rule.{h,cpp} (allow/disallow rules with an optional OS
// condition; the OS check delegates to RuntimeContext::classifierMatches).

using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Minecraft;

public class Rule
{
    public enum Action
    {
        Allow,
        Disallow,
        Defer,
    }

    private struct Os
    {
        public string Name;

        // FIXME upstream: unsupported
        // retained to avoid information being lost from files
        public string Version;
    }

    private Action _action = Action.Defer;

    private Os? _os;

    public static Rule FromJson(JsonObject obj)
    {
        RuleJson? dto;
        try
        {
            dto = obj.Deserialize(VersionFormatJsonContext.Default.RuleJson);
        }
        catch (JsonException e)
        {
            throw new JsonException("Rule: Error parsing JSON: " + e.Message);
        }

        return VersionFormatMapper.MapRule(
            dto ?? throw new JsonException("Rule: Error parsing JSON: document is null"));
    }

    /// <summary>The typed-probe seam (RuleJson arrives from the version format readers).</summary>
    internal static Rule FromWire(Action action, string? osName, string osVersion)
    {
        var result = new Rule { _action = action };
        if (osName is not null)
        {
            result._os = new Os
            {
                Name = osName,
                Version = osVersion,
            };
        }

        return result;
    }

    /// <summary>The typed write wire (the former ToJson node): the action string — a Defer
    /// rule writes nothing — and the os object when present.</summary>
    internal RuleOutJson ToWire()
    {
        RuleOsOutJson? osWire = null;
        if (_os is { } ruleOs)
        {
            osWire = new RuleOsOutJson
            {
                Name = ruleOs.Name,
                Version = ruleOs.Version.Length != 0 ? ruleOs.Version : null,
            };
        }

        return new RuleOutJson
        {
            Action = _action switch
            {
                Action.Allow => "allow",
                Action.Disallow => "disallow",
                _ => null,
            },
            Os = osWire,
        };
    }

    public Action Apply(RuntimeContext runtimeContext)
    {
        if (this._os is { } ruleOs && !runtimeContext.ClassifierMatches(ruleOs.Name))
        {
            return Action.Defer;
        }

        return _action;
    }
}
