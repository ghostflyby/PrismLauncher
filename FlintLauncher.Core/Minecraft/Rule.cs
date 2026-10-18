// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/Rule.{h,cpp} (allow/disallow rules with an optional OS
// condition; the OS check delegates to RuntimeContext::classifierMatches).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;

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

    private Action action = Action.Defer;

    private Os? os;

    public static Rule FromJson(JsonObject obj)
    {
        RuleJson? dto;
        try
        {
            dto = JsonSerializer.Deserialize(obj, VersionFormatJsonContext.Default.RuleJson)
                  ?? throw new JsonParseException("Rule: Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException("Rule: Error parsing JSON: " + e.Message);
        }

        return VersionFormatMapper.MapRule(dto);
    }

    /// <summary>The typed-probe seam (RuleJson arrives from the version format readers).</summary>
    internal static Rule FromWire(Action action, string? osName, string osVersion)
    {
        var result = new Rule { action = action };
        if (osName is not null)
        {
            result.os = new Os
            {
                Name = osName,
                Version = osVersion,
            };
        }

        return result;
    }

    public JsonObject ToJson()
    {
        var result = new JsonObject();

        if (action == Action.Allow)
        {
            result["action"] = JsonValue.Create("allow");
        }
        else if (action == Action.Disallow)
        {
            result["action"] = JsonValue.Create("disallow");
        }

        if (this.os is { } os)
        {
            var osObj = new JsonObject
            {
                ["name"] = JsonValue.Create(os.Name),
            };
            if (os.Version.Length != 0)
            {
                osObj["version"] = JsonValue.Create(os.Version);
            }

            result["os"] = osObj;
        }

        return result;
    }

    public Action Apply(RuntimeContext runtimeContext)
    {
        if (this.os is { } os && !runtimeContext.ClassifierMatches(os.Name))
        {
            return Action.Defer;
        }

        return action;
    }
}
