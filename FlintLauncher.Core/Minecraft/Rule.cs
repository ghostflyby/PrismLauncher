// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/Rule.{h,cpp} (allow/disallow rules with an optional OS
// condition; the OS check delegates to RuntimeContext::classifierMatches).

using System.Text.Json.Nodes;
using FlintLauncher.Core.Java;

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

    /// <summary>true when the JSON string member equals the given text (QJsonValue == QString).</summary>
    private static bool StringIs(JsonObject obj, string key, string value)
    {
        return obj.TryGetPropertyValue(key, out var node) && JsonObjectExtensions.StringOr(node) == value;
    }

    public static Rule FromJson(JsonObject obj)
    {
        var result = new Rule();

        if (StringIs(obj, "action", "allow"))
        {
            result.action = Action.Allow;
        }
        else if (StringIs(obj, "action", "disallow"))
        {
            result.action = Action.Disallow;
        }

        if (obj.TryGetPropertyValue("os", out var os) && os is JsonObject osObj)
        {
            if (osObj.TryGetPropertyValue("name", out var name) &&
                name is JsonValue nameValue &&
                nameValue.TryGetValue<string>(out var nameStr))
            {
                result.os = new Os
                {
                    Name = nameStr,
                    Version = osObj.StringOr("version"),
                };
            }
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
