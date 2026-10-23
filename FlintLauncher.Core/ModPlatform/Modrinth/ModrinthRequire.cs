// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

/// <summary>Required-member checks over the parsed wire DTOs, mirroring the former
/// requireIsType* messages the former node readers produced (the "key is not a …" half; the
/// DTO cannot tell a missing key from a wrong-kind one, so both collapse onto the kind
/// message).</summary>
internal static class ModrinthRequire
{
    public static string String(string? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not a string");

    public static bool Boolean(bool? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not a bool");

    public static List<T> Array<T>(List<T>? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not an array");

    public static Dictionary<string, string> Object(Dictionary<string, string>? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not an object");
}
