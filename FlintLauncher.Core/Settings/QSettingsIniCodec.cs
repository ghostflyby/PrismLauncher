// SPDX-License-Identifier: GPL-3.0-only
// C# reimplementation of Qt's INI format codec (QSettings::IniFormat) — READ side only since
// CONFIG C5. Verified against golden files produced by real QSettings 6.11 (see
// tools/qt-ini-golden and tests/FlintLauncher.Tests/TestData/IniFile) plus the per-file dump
// fixtures recording what QSettings itself reads back. Covers the value shapes PrismLauncher
// stores; the QDataStream-backed @-types are preserved verbatim instead of being decoded.
//
// Consumers are the import-only INI readers (FlintConfigImporter for the global chain,
// InstanceIniImporter for instance.cfg) — INI files are never deleted, never rewritten by
// Flint (flint.json / instance.json are the only config write targets, TypedJsonFile).
// The former Encode/SaveFile writer half retired with the instance.cfg write path (D4).
namespace FlintLauncher.Core.Settings;

internal static class QSettingsIniCodec
{
    public const string ConfigVersionKey = "ConfigVersion";
    public const string CurrentConfigVersion = "1.3";

    // ---------------------------------------------------------------- reading

    public static Dictionary<string, IniValue> Parse(string text)
    {
        var map = new Dictionary<string, IniValue>(StringComparer.Ordinal);
        var prefix = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var sectionText = trimmed[1..^1].Trim();
                // Only the literal (case-insensitive) [General] maps to the root; [%General] is
                // the escaped form of a real group named "General".
                prefix = sectionText.Equals("General", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : UnescapeKey(sectionText) + "/";
                continue;
            }

            // first '=' outside quotes separates key from value
            var eqPos = -1;
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == '=' && !inQuotes)
                {
                    eqPos = i;
                    break;
                }
            }
            if (eqPos == -1)
            {
                continue;
            }
            var key = UnescapeKey(line[..eqPos].Trim());
            if (key.Length == 0)
            {
                continue;
            }
            map[prefix + key] = ParseValue(line[(eqPos + 1)..]);
        }
        return map;
    }

    private static IniValue ParseValue(string valueText)
    {
        // @-typed values are kept verbatim (no list splitting, no unescaping) so they round-trip.
        var trimmed = valueText.TrimStart();
        if (trimmed.StartsWith('@'))
        {
            return StringToValue(trimmed);
        }
        var elements = ParseStringList(valueText);
        if (elements.Count > 1)
        {
            return IniValue.From(elements);
        }
        var s = elements.Count == 1 ? elements[0] : "";
        return StringToValue(s);
    }

    private static IniValue StringToValue(string s)
    {
        if (s == "@Invalid()")
        {
            return IniValue.Invalid;
        }
        if (s.StartsWith("@ByteArray(", StringComparison.Ordinal) && s.EndsWith(')'))
        {
            var inner = UnescapeContent(s["@ByteArray(".Length..^1]);
            var bytes = new byte[inner.Length];
            for (var i = 0; i < inner.Length; i++)
            {
                bytes[i] = (byte)(inner[i] & 0xFF);
            }
            return IniValue.From(bytes);
        }
        if (s.StartsWith("@String(", StringComparison.Ordinal) && s.EndsWith(')'))
        {
            // @String(<escaped content>): Qt's wrapper for QStrings whose plain INI form would
            // be ambiguous (embedded NUL and friends — e.g. @String(ab\0\x63\x64)). Qt's read
            // side unwraps it back to the plain string, so the import must too; the escaping
            // is the same ini escape set UnescapeContent covers (same as @ByteArray).
            return IniValue.From(UnescapeContent(s["@String(".Length..^1]));
        }
        if (s.StartsWith("@@", StringComparison.Ordinal))
        {
            return IniValue.From(s[1..]);
        }
        if (s.StartsWith('@'))
        {
            // @Variant/@Point/@Rect/@Size/@DateTime(...): keep verbatim; QDataStream-backed
            // shapes are not needed by the launcher core and would not round-trip if decoded.
            return IniValue.Raw(s);
        }
        return IniValue.From(s);
    }

    /// <summary>Port of QSettings' iniUnescapedStringList state machine (comma-separated elements).</summary>
    private static List<string> ParseStringList(string value)
    {
        var result = new List<string>();
            var i = 0;
            var n = value.Length;
            while (true)
            {
                // skip leading spaces of the element
                while (i < n && value[i] == ' ')
                {
                    i++;
                }
                if (i >= n)
                {
                    result.Add("");
                    return result;
                }

            var current = new System.Text.StringBuilder();
            var anyQuoted = false;
            var hadContent = false;
            // an element is a mix of quoted and unquoted parts ("ab" cd "ef" ->abcdef)
            while (i < n)
            {
                while (i < n && value[i] == ' ')
                {
                    i++;
                }
                if (i >= n)
                {
                    break;
                }
                if (value[i] == ',')
                {
                    break;
                }
                if (value[i] == '"')
                {
                    anyQuoted = true;
                    hadContent = true;
                    i++;
                    while (i < n)
                    {
                        var c = value[i];
                        if (c == '"')
                        {
                            i++;
                            break;
                        }
                        if (c == '\\' && i + 1 < n)
                        {
                            i = ReadEscape(value, i + 1, current);
                            continue;
                        }
                        current.Append(c);
                        i++;
                    }
                    continue;
                }
                // unquoted chunk: runs until top-level comma or end
                var chunkStart = current.Length;
                var end = i;
                while (end < n && value[end] != ',')
                {
                    end++;
                }
                var chunk = UnescapeContent(value[i..end]);
                // chop trailing spaces of unquoted chunks
                var chopEnd = chunk.Length;
                while (chopEnd > 0 && chunk[chopEnd - 1] == ' ')
                {
                    chopEnd--;
                }
                current.Append(chunk[..chopEnd]);
                i = end;
                if (current.Length > chunkStart)
                {
                    hadContent = true;
                }
                else if (chunk.Length > 0)
                {
                    hadContent = true;
                }
            }
            if (!hadContent && current.Length == 0 && !anyQuoted)
            {
                // keep empty element for e.g. ", b" at position 0
            }
            result.Add(current.ToString());

            // skip separator
            var j = i;
            while (j < n && value[j] == ' ')
            {
                j++;
            }
            if (j < n && value[j] == ',')
            {
                i = j + 1;
                continue;
            }
            return result;
        }
    }

    /// <summary>Reads one escape sequence starting after the backslash; returns the new index.</summary>
    private static int ReadEscape(string s, int i, System.Text.StringBuilder sb)
    {
        if (i >= s.Length)
        {
            return i;
        }
        var c = s[i];
        if (c == 'x')
        {
            i++;
            var val = 0;
            var any = false;
            while (i < s.Length && Uri.IsHexDigit(s[i]))
            {
                val = (val << 4) + Uri.FromHex(s[i]);
                i++;
                any = true;
            }
            sb.Append(any ? (char)val : 'x');
            return i;
        }
        if (c >= '0' && c <= '7')
        {
            var val = 0;
            var digits = 0;
            while (i < s.Length && digits < 3 && s[i] >= '0' && s[i] <= '7')
            {
                val = (val << 3) + (s[i] - '0');
                i++;
                digits++;
            }
            sb.Append((char)val);
            return i;
        }
        var decoded = c switch
        {
            'a' => '\a',
            'b' => '\b',
            'f' => '\f',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'v' => '\v',
            '"' => '"',
            '?' => '?',
            '\'' => '\'',
            '\\' => '\\',
            _ => c,
        };
        sb.Append(decoded);
        return i + 1;
    }

    private static string UnescapeContent(string s)
    {
        if (!s.Contains('\\'))
        {
            return s;
        }
        var sb = new System.Text.StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                i = ReadEscape(s, i + 1, sb);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Port of QSettings' iniUnescapedKey: '\\' becomes '/', %XX/%UXXXX decode characters.</summary>
    private static string UnescapeKey(string key)
    {
        var sb = new System.Text.StringBuilder();
        var byteRun = new List<byte>();
        var i = 0;
        void FlushByteRun()
        {
            if (byteRun.Count == 0)
            {
                return;
            }
            // %XX escapes may form UTF-8 sequences; decode them, falling back to Latin-1
            try
            {
                sb.Append(System.Text.Encoding.UTF8.GetString(byteRun.ToArray()));
            }
            catch (Exception)
            {
                foreach (var b in byteRun)
                {
                    sb.Append((char)b);
                }
            }
            byteRun.Clear();
        }
        while (i < key.Length)
        {
            var c = key[i];
            if (c == '%' && i == 0 && !(i + 1 < key.Length && (Uri.IsHexDigit(key[i + 1]) || key[i + 1] == 'U')))
            {
                // Qt writes real "General" groups as [%General]: a leading % that is not an
                // escape is a marker and is dropped.
                i++;
            }
            else if (c == '%' && i + 1 < key.Length)
            {
                if (key[i + 1] == 'U' && i + 5 < key.Length &&
                    Uri.IsHexDigit(key[i + 2]) && Uri.IsHexDigit(key[i + 3]) &&
                    Uri.IsHexDigit(key[i + 4]) && Uri.IsHexDigit(key[i + 5]))
                {
                    FlushByteRun();
                    sb.Append((char)Convert.ToInt32(key.Substring(i + 2, 4), 16));
                    i += 6;
                    continue;
                }
                if (i + 3 < key.Length && Uri.IsHexDigit(key[i + 1]) && Uri.IsHexDigit(key[i + 2]))
                {
                    byteRun.Add((byte)Convert.ToInt32(key.Substring(i + 1, 2), 16));
                    i += 3;
                    continue;
                }
                FlushByteRun();
                sb.Append(c);
                i++;
            }
            else if (c == '\\')
            {
                FlushByteRun();
                sb.Append('/');
                i++;
            }
            else
            {
                FlushByteRun();
                sb.Append(c);
                i++;
            }
        }
        FlushByteRun();
        return sb.ToString();
    }
}
