// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of launcher/minecraft/ProfileUtils.{h,cpp}: instance-format independent helpers around
// OneSix version/order files.
//
// Deviations from the C++ original:
//  - parseJsonFile() takes the file path instead of a QFileInfo; the error version file ids
//    use the same completeBaseName() rule (everything up to the last '.').
//  - The QJsonParseError line/column scan is replaced by the line info carried by
//    System.Text.Json's JsonException (message texts differ).
//  - saveJsonFile() writes through the atomic FileOps.Write (QSaveFile equivalent).
//  - writeOverrideOrders() is declared in the C++ header but has no definition anywhere in
//    this checkout; it is not ported (dead declaration upstream).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Minecraft;

/// <summary>The dt-order/override order file shape ({"version":N,"order":[uid,...]}).</summary>
internal sealed record OverrideOrderFileJson(
    [property: JsonPropertyName("version")]
    int? Version,
    [property: JsonPropertyName("order")] List<string>? Order);

[JsonSerializable(typeof(OverrideOrderFileJson))]
internal sealed partial class ProfileUtilsJsonContext : JsonSerializerContext;

public static class ProfileUtils
{
    private const int CurrentOrderFileVersion = 1;

    /// <summary>Port of ProfileUtils::readOverrideOrders.</summary>
    public static bool ReadOverrideOrders(string path, List<string> order)
    {
        if (!File.Exists(path))
        {
            // qWarning() << "Order file doesn't exist. Ignoring.";
            return false;
        }

        string data;
        try
        {
            data = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // qCritical() << "Couldn't open" << ... << "for reading:"
            return false;
        }

        // and it's valid JSON (typed: a malformed file fails whole, like the require* reads)
        OverrideOrderFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(data, ProfileUtilsJsonContext.Default.OverrideOrderFileJson);
        }
        catch (JsonException)
        {
            file = null;
        }

        // check order file version.
        if (file?.Version is not { } version || version != CurrentOrderFileVersion)
        {
            // qCritical() << "Couldn't parse" << ... << ": bad file format"
            order.Clear();
            return false;
        }

        if (file.Order is { } entries)
        {
            order.AddRange(entries);
        }

        return true;
    }

    private static string CompleteBaseName(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static VersionFile CreateErrorVersionFile(string fileId, string error)
    {
        var outError = new VersionFile();
        outError.Uid = outError.Name = fileId;
        outError.AddProblem(ProblemSeverity.Error, error);
        return outError;
    }

    private static VersionFile GuardedParseJson(JsonNode? root, string fileId, string filepath, bool requireOrder)
    {
        try
        {
            return OneSixVersionFormat.VersionFileFromJson(
                root as JsonObject ?? throw new JsonException("Value is not an object"),
                filepath, requireOrder);
        }
        catch (Exception e) when (e is JsonException)
        {
            return CreateErrorVersionFile(fileId, e.Message);
        }
    }

    /// <summary>Port of ProfileUtils::parseJsonFile. Never throws: failures yield a VersionFile carrying a problem.</summary>
    public static VersionFile ParseJsonFile(string filePath, bool requireOrder)
    {
        string data;
        try
        {
            data = File.ReadAllText(filePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException)
        {
            var errorStr = $"Unable to open the version file {Path.GetFileName(filePath)}: {e.Message}.";
            return CreateErrorVersionFile(CompleteBaseName(filePath), errorStr);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(data);
        }
        catch (JsonException e)
        {
            // the C++ code scans up to the parse error offset for line/column; System.Text.Json
            // carries the position on the exception (both are 0-based there, hence the +1)
            var line = (e.LineNumber ?? 0) + 1;
            var column = e.BytePositionInLine ?? 0;
            var errorStr = $"Unable to process the version file {Path.GetFileName(filePath)}: {e.Message} at line {line} column {column}.";
            return CreateErrorVersionFile(CompleteBaseName(filePath), errorStr);
        }

        return GuardedParseJson(root, CompleteBaseName(filePath), filePath, requireOrder);
    }

    /// <summary>Port of ProfileUtils::saveJsonFile — the typed patch wire (the former
    /// QtJson.ToIndentedString over the node document).</summary>
    internal static bool SaveJsonFile(VersionFileOutJson doc, string filename)
    {
        try
        {
            FileOps.Write(new AbsolutePath(filename),
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(doc, VersionFormatJsonContext.Default.VersionFileOutJson)));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            // qWarning() << "Couldn't open/save" << filename
            return false;
        }
    }

    /// <summary>Port of ProfileUtils::removeLwjglFromPatch.</summary>
    public static void RemoveLwjglFromPatch(VersionFile patch)
    {
        patch.Libraries.RemoveAll(lib => VersionFilterData.Instance.LwjglWhitelist.Contains(lib.ArtifactPrefix()));
    }
}
