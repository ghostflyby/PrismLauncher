// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2023 flowln <flowlnlnln@gmail.com>
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
// Port of launcher/settings/INIFile.{h,cpp}: sectionless INI storage. This is the READ
// side only — the one-shot INI imports (FlintConfigImporter for the global
// chain, InstanceIniImporter for instance.cfg). Flint never writes an INI; the write sides
// are flint.json and instance.json (TypedJsonFile). Reads legacy formats (no ConfigVersion,
// 1.1, 1.2), staying byte-compatible with PrismLauncher.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Settings;

public sealed class IniFile : IReadOnlyDictionary<string, IniValue>
{
    private readonly Dictionary<string, IniValue> _map = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ access

    public int Count => _map.Count;

    public IReadOnlyCollection<string> Keys => _map.Keys;

    public IReadOnlyCollection<IniValue> Values => _map.Values;

    IEnumerable<string> IReadOnlyDictionary<string, IniValue>.Keys => Keys;

    IEnumerable<IniValue> IReadOnlyDictionary<string, IniValue>.Values => Values;

    public IniValue this[string key] => _map[key];

    public bool ContainsKey(string key) => _map.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out IniValue value) => _map.TryGetValue(key, out value);

    public string GetString(string key, string def)
    {
        return _map.TryGetValue(key, out var value) && value.ValueKind == IniValue.Kind.String
            ? value.String
            : def;
    }

    public IEnumerator<KeyValuePair<string, IniValue>> GetEnumerator() => _map.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ------------------------------------------------------------------- file io

    public bool LoadFile(AbsolutePath fileName)
    {
        if (!fileName.ExistsFile())
        {
            return false;
        }

        string text;
        try
        {
            text = fileName.ReadAllText(Encoding.UTF8);
        }
        catch (IOException)
        {
            return false;
        }

        return LoadText(text);
    }

    public bool LoadText(string text)
    {
        _map.Clear();
        var parsed = QSettingsIniCodec.Parse(text);

        if (!parsed.TryGetValue(QSettingsIniCodec.ConfigVersionKey, out var versionValue) ||
            versionValue.ValueKind != IniValue.Kind.String)
        {
            // pre-1.1 legacy format: custom "key=value" parser with escape quirks
            foreach (var (key, value) in ParseOldFileFormat(text))
            {
                _map[key] = MigrateQByteArrayToBase64(key, value);
            }

            _map[QSettingsIniCodec.ConfigVersionKey] = IniValue.From(QSettingsIniCodec.CurrentConfigVersion);
        }
        else
        {
            var version = versionValue.String;
            if (version == "1.1")
            {
                foreach (var (key, value) in parsed)
                {
                    var migrated = MigrateQByteArrayToBase64(key, value);
                    if (migrated.ValueKind == IniValue.Kind.String)
                    {
                        var valueStr = migrated.String;
                        if ((valueStr.Contains(';') || valueStr.Contains('=') || valueStr.Contains(',')) &&
                            valueStr.EndsWith('"') && valueStr.StartsWith('"'))
                        {
                            _map[key] = IniValue.From(Unquote(valueStr));
                            continue;
                        }
                    }

                    _map[key] = migrated;
                }

                _map[QSettingsIniCodec.ConfigVersionKey] = IniValue.From(QSettingsIniCodec.CurrentConfigVersion);
            }
            else if (version == "1.2")
            {
                foreach (var (key, value) in parsed)
                {
                    _map[key] = MigrateQByteArrayToBase64(key, value);
                }

                _map[QSettingsIniCodec.ConfigVersionKey] = IniValue.From(QSettingsIniCodec.CurrentConfigVersion);
            }
            else
            {
                foreach (var (key, value) in parsed)
                {
                    _map[key] = value;
                }
            }
        }

        return true;
    }

    // ------------------------------------------------------------- legacy formats

    /// <summary>Port of INIFile.cpp parseOldFileFormat, including its comment-scanning quirks.</summary>
    private static Dictionary<string, IniValue> ParseOldFileFormat(string text)
    {
        var map = new Dictionary<string, IniValue>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        foreach (var lineRaw in lines)
        {
            var line = lineRaw;
            // Search for comments until no more escaped # are available
            var commentIndex = 0;
            while (true)
            {
                var searchStart = commentIndex + 1;
                if (searchStart > line.Length)
                {
                    break;
                }

                commentIndex = line.IndexOf('#', searchStart);
                if (commentIndex == -1)
                {
                    break;
                }

                if (commentIndex > 0 && line[commentIndex - 1] == '\\')
                {
                    continue;
                }

                line = line[..lineRaw.IndexOf('#')].Trim();
            }

            var eqPos = line.IndexOf('=', StringComparison.Ordinal);
            if (eqPos == -1)
            {
                continue;
            }

            var key = line[..eqPos].Trim();
            var valueStr = line[(eqPos + 1)..].Trim();

            valueStr = Unquote(UnescapeLegacy(valueStr));

            map[key] = IniValue.From(valueStr);
        }

        return map;
    }

    private static string UnescapeLegacy(string orig)
    {
        var sb = new StringBuilder();
        char prev = '\0';
        foreach (var c in orig)
        {
            if (prev == '\\')
            {
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '#': sb.Append('#'); break;
                    default: sb.Append(c); break;
                }

                prev = '\0';
            }
            else
            {
                if (c == '\\')
                {
                    prev = c;
                    continue;
                }

                sb.Append(c);
                prev = '\0';
            }
        }

        return sb.ToString();
    }

    private static string Unquote(string str)
    {
        if ((str.Contains(';') || str.Contains('=') || str.Contains(',')) && str.EndsWith('"') && str.StartsWith('"'))
        {
            str = str[1..^1];
        }

        return str;
    }

    // Keys whose QByteArray values migrate to base64 text (or raw text) in 1.1/1.2 formats.
    private static readonly string[] OtherByteArrays =
    [
        "MainWindowState", "MainWindowGeometry", "ConsoleWindowState", "ConsoleWindowGeometry",
        "PagedGeometry", "NewInstanceGeometry", "ModDownloadGeometry", "RPDownloadGeometry",
        "TPDownloadGeometry", "ShaderDownloadGeometry",
    ];

    /// <summary>Port of migrateQByteArrayToBase64.</summary>
    private static IniValue MigrateQByteArrayToBase64(string key, IniValue value)
    {
        if (value.ValueKind == IniValue.Kind.ByteArray)
        {
            // WideBarVisibility_* base64s like the C++ migration table did when this port was
            // made (upstream added it in 7e174f53a, removed with the feature in 3d006bd0b) —
            // kept so older Prism cfgs still migrate; no importer consumes these keys.
            // s_otherByteArrays uses the lossless per-byte Latin-1 form where C++'s
            // QString::fromUtf8 substitutes U+FFFD into binary state; neither side's migrated
            // value is ever read back.
            if (key.StartsWith("WideBarVisibility_", StringComparison.Ordinal) ||
                (key.StartsWith("UI/", StringComparison.Ordinal) && key.EndsWith("_Page/Columns", StringComparison.Ordinal)))
            {
                return IniValue.From(Convert.ToBase64String(value.Bytes));
            }

            if (OtherByteArrays.Contains(key))
            {
                var sb = new StringBuilder(value.Bytes.Length);
                foreach (var b in value.Bytes)
                {
                    sb.Append((char)b);
                }

                return IniValue.From(sb.ToString());
            }

            return value;
        }

        switch (key)
        {
            case "linkedInstances":
            {
                // Json::fromStringList over the string list (a single string converts to a 1-item list)
                var items = value.ValueKind switch
                {
                    IniValue.Kind.StringList => value.List,
                    IniValue.Kind.String => [value.String],
                    _ => null,
                };
                if (items is null) return value;
                var array = new JsonArray();
                foreach (var item in items)
                {
                    array.Add((JsonNode?)JsonValue.Create(item));
                }

                return IniValue.From(array.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
            case "Env":
                // Json::fromMap over QVariantMap; INI-stored maps only come through @Variant blobs,
                // which we keep verbatim, so the only reachable shape is the empty map.
                return value.ValueKind == IniValue.Kind.Raw ? value : IniValue.From("{}");
            default:
                return value;
        }
    }
}
