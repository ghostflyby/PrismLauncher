// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/ProfileUtils.{h,cpp}: instance-format independent helpers around
// OneSix version/order files.
//
// Deviations from the C++ original:
//  - parseJsonFile() takes the file path instead of a QFileInfo; the error version file ids
//    use the same completeBaseName() rule (everything up to the last '.').
//  - The QJsonParseError line/column scan is replaced by the line info carried by
//    System.Text.Json's JsonException (message texts differ).
//  - saveJsonFile() writes through the atomic FileSystem.Write (QSaveFile equivalent).
//  - writeOverrideOrders() is declared in the C++ header but has no definition anywhere in
//    this checkout; it is not ported (dead declaration upstream).
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.Minecraft;

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

        // and it's valid JSON
        try
        {
            using var doc = JsonUtils.RequireDocument(data, path);
            var obj = JsonUtils.RequireObject(doc).ToJsonObject();
            // check order file version.
            var version = JsonUtils.RequireInteger(obj, "version");
            if (version != CurrentOrderFileVersion)
            {
                throw new JsonParseException($"Invalid order file version, expected {CurrentOrderFileVersion}");
            }
            var orderArray = JsonUtils.RequireArray(obj, "order");
            foreach (var item in orderArray.EnumerateArray())
            {
                order.Add(JsonUtils.RequireString(item));
            }
        }
        catch (JsonParseException)
        {
            // qCritical() << "Couldn't parse" << ... << ": bad file format"
            order.Clear();
            return false;
        }
        return true;
    }

    private static string CompleteBaseName(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static VersionFile CreateErrorVersionFile(string fileId, string filepath, string error)
    {
        var outError = new VersionFile();
        outError.Uid = outError.Name = fileId;
        outError.AddProblem(ProblemSeverity.Error, error);
        return outError;
    }

    private static VersionFile GuardedParseJson(JsonDocument doc, string fileId, string filepath, bool requireOrder)
    {
        try
        {
            return OneSixVersionFormat.VersionFileFromJson(doc.RootElement.ToJsonObject(), filepath, requireOrder);
        }
        catch (Exception e) when (e is JsonParseException or JsonException)
        {
            return CreateErrorVersionFile(fileId, filepath, e.Message);
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
            return CreateErrorVersionFile(CompleteBaseName(filePath), filePath, errorStr);
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException e)
        {
            // the C++ code scans up to the parse error offset for line/column; System.Text.Json
            // carries the position on the exception (both are 0-based there, hence the +1)
            var line = (e.LineNumber ?? 0) + 1;
            var column = e.BytePositionInLine ?? 0;
            var errorStr = $"Unable to process the version file {Path.GetFileName(filePath)}: {e.Message} at line {line} column {column}.";
            return CreateErrorVersionFile(CompleteBaseName(filePath), filePath, errorStr);
        }
        using (doc)
        {
            return GuardedParseJson(doc, CompleteBaseName(filePath), filePath, requireOrder);
        }
    }

    /// <summary>Port of ProfileUtils::saveJsonFile (indented, Qt-style JSON like QJsonDocument::toJson).</summary>
    public static bool SaveJsonFile(JsonObject doc, string filename)
    {
        try
        {
            // QJsonDocument::toJson() always appends a trailing newline
            FileSystem.Write(filename, System.Text.Encoding.UTF8.GetBytes(QtJson.ToIndentedString(doc) + "\n"));
            return true;
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
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
