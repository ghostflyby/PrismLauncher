// SPDX-License-Identifier: GPL-3.0-only
// A minimal, AOT-safe TOML subset parser (hand-rolled replacement for toml++) used by the
// packwiz metadata layer. It covers exactly the constructs packwiz .pw.toml files (and the
// writer in Packwiz.cs) use:
//   - comments (# to end of line), blank lines
//   - tables: [table] and dotted [table.subtable] headers
//   - arrays of tables: [[table]] and dotted [[table.subtable]] headers (appended in order)
//   - key = value pairs with bare keys (letters, digits, '_', '-'; dotted keys supported)
//   - basic strings "..." with the standard escape set (\\, \", \b, \t, \n, \f, \r,
//     \uXXXX, \UXXXXXXXX) and literal strings '...'
//   - integers (64-bit decimal, optional sign)
//   - booleans
//   - arrays of any value, possibly spanning multiple lines, with an optional trailing comma
//   - inline tables { key = value, key = value }
// Everything else (floats, datetimes, multiline strings, nested brackets in keys, ...)
// is rejected with a parse error, like the C++ callers would see from toml++'s parse_error.
namespace FlintLauncher.Core.ModPlatform.Packwiz;

/// <summary>Port of toml++'s parse_error for the subset parser.</summary>
public sealed class TomlParseException(string message) : Exception(message)
{
}

/// <summary>
/// A parsed TOML table: an ordered map of keys to values, where a value is a
/// <see cref="string"/>, <see cref="long"/>, <see cref="bool"/>, <see cref="List{T}"/>
/// (of values) or a nested <see cref="TomlTable"/>.
/// </summary>
public sealed class TomlTable
{
    private readonly Dictionary<string, object?> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public bool TryGetValue(string key, out object? value) => _entries.TryGetValue(key, out value);

    /// <summary>The keys of the table, in insertion order (toml++ table iteration).</summary>
    public IReadOnlyCollection<string> Keys() => _entries.Keys;

    public void Set(string key, object? value) => _entries[key] = value;

    /// <summary>The string value at key, or "" when missing/not a string (node->value_or("")).</summary>
    public string GetString(string key)
    {
        if (_entries.TryGetValue(key, out var value) && value is string s)
        {
            return s;
        }
        return "";
    }

    /// <summary>The integer value at key, or 0 when missing/not an integer (node->value_or(0)).</summary>
    public long GetInteger(string key)
    {
        if (_entries.TryGetValue(key, out var value) && value is long l)
        {
            return l;
        }
        return 0;
    }

    /// <summary>The boolean value at key, or false when missing/not a boolean.</summary>
    public bool GetBoolean(string key)
    {
        if (_entries.TryGetValue(key, out var value) && value is bool b)
        {
            return b;
        }
        return false;
    }

    /// <summary>The sub-table at key, or null (table["key"].as_table()).</summary>
    public TomlTable? GetTable(string key)
    {
        if (_entries.TryGetValue(key, out var value) && value is TomlTable table)
        {
            return table;
        }
        return null;
    }

    /// <summary>The array at key, or null (table["key"].as_array()).</summary>
    public IReadOnlyList<object?>? GetArray(string key)
    {
        if (_entries.TryGetValue(key, out var value) && value is List<object?> array)
        {
            return array;
        }
        return null;
    }

    /// <summary>The string elements of the array at key (non-string elements are skipped, like the C++).</summary>
    public List<string> GetStringArray(string key)
    {
        var result = new List<string>();
        if (GetArray(key) is { } array)
        {
            foreach (var element in array)
            {
                if (element is string s)
                {
                    result.Add(s);
                }
            }
        }
        return result;
    }

    /// <summary>The table elements of the array at key (non-table elements are skipped).</summary>
    public List<TomlTable> GetTableArray(string key)
    {
        var result = new List<TomlTable>();
        if (GetArray(key) is { } array)
        {
            foreach (var element in array)
            {
                if (element is TomlTable table)
                {
                    result.Add(table);
                }
            }
        }
        return result;
    }
}

public static class MiniToml
{
    /// <summary>toml::parse_file equivalent: parse the file content into its root table.</summary>
    public static TomlTable ParseFile(string path) => Parse(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));

    /// <summary>toml::parse equivalent for the documented subset.</summary>
    public static TomlTable Parse(string text)
    {
        return new Parser(text).ParseDocument();
    }

    private sealed class Parser(string text)
    {
        private int _pos;
        private TomlTable _current = new();
        private readonly TomlTable _root = new();

        // tables explicitly opened via [header]; tables created implicitly by dotted keys
        // do not count as "defined" in TOML, but the subset does not need that distinction
        private readonly HashSet<string> _definedTables = new(StringComparer.Ordinal);

        internal TomlTable ParseDocument()
        {
            _current = _root;
            while (true)
            {
                SkipWhitespaceAndComments();
                if (AtEnd)
                {
                    return _root;
                }
                var c = Peek();
                if (c == '[')
                {
                    if (Peek(1) == '[')
                    {
                        ParseArrayTableHeader();
                    }
                    else
                    {
                        ParseTableHeader();
                    }
                }
                else
                {
                    ParseKeyValue(_current);
                }
            }
        }

        private char Peek(int offset = 0)
        {
            var index = _pos + offset;
            return index < text.Length ? text[index] : '\0';
        }

        private bool AtEnd => _pos >= text.Length;

        private char Advance()
        {
            var c = text[_pos];
            _pos++;
            return c;
        }

        private void SkipWhitespaceAndComments()
        {
            while (!AtEnd)
            {
                var c = Peek();
                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    Advance();
                }
                else if (c == '#')
                {
                    while (!AtEnd && Peek() != '\n')
                    {
                        Advance();
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private void SkipInlineWhitespace()
        {
            while (!AtEnd && Peek() is ' ' or '\t')
            {
                Advance();
            }
        }

        private void ParseTableHeader()
        {
            Expect('[');
            _current = _root;
            var path = new List<string>();
            while (true)
            {
                SkipInlineWhitespace();
                path.Add(ParseKeyPart());
                SkipInlineWhitespace();
                if (Peek() == '.')
                {
                    Advance();
                    continue;
                }
                Expect(']');
                break;
            }

            var table = _root;
            foreach (var part in path)
            {
                var existing = table.GetTable(part);
                if (existing is null)
                {
                    if (table.TryGetValue(part, out var clash))
                    {
                        throw new TomlParseException($"Key '{part}' is not a table");
                    }
                    existing = new TomlTable();
                    table.Set(part, existing);
                }
                table = existing;
            }
            var header = string.Join('.', path);
            if (!_definedTables.Add(header))
            {
                throw new TomlParseException($"Duplicate table definition: [{header}]");
            }
            _current = table;
        }

        /// <summary>
        /// [[array of tables]] header: append a fresh table to the (array) value at the
        /// dotted path, creating intermediate tables, and make it the current table.
        /// Repeated [[header]]s are legal (unlike [header]), so the duplicate-table check
        /// does not apply here.
        /// </summary>
        private void ParseArrayTableHeader()
        {
            Expect('[');
            Expect('[');
            var path = new List<string>();
            while (true)
            {
                SkipInlineWhitespace();
                path.Add(ParseKeyPart());
                SkipInlineWhitespace();
                if (Peek() == '.')
                {
                    Advance();
                    continue;
                }
                Expect(']');
                Expect(']');
                break;
            }

            // walk/create the intermediate tables
            var parent = _root;
            for (var i = 0; i < path.Count - 1; i++)
            {
                var existing = parent.GetTable(path[i]);
                if (existing is null)
                {
                    if (parent.TryGetValue(path[i], out var clash))
                    {
                        throw new TomlParseException($"Key '{path[i]}' is not a table");
                    }
                    existing = new TomlTable();
                    parent.Set(path[i], existing);
                }
                parent = existing;
            }

            var lastKey = path[^1];
            List<object?>? array;
            if (parent.TryGetValue(lastKey, out var existingValue))
            {
                if (existingValue is not List<object?> existingList)
                {
                    throw new TomlParseException($"Key '{lastKey}' is not an array of tables");
                }
                array = existingList;
            }
            else
            {
                array = [];
                parent.Set(lastKey, array);
            }

            var element = new TomlTable();
            array.Add(element);
            _current = element;
        }

        private void ParseKeyValue(TomlTable table, bool inline = false)
        {
            // dotted keys: a.b.c = value creates the intermediate tables
            var target = table;
            while (true)
            {
                var part = ParseKeyPart();
                SkipInlineWhitespace();
                if (Peek() == '.')
                {
                    Advance();
                    var sub = target.GetTable(part);
                    if (sub is null)
                    {
                        if (target.TryGetValue(part, out var clash))
                        {
                            throw new TomlParseException($"Key '{part}' is not a table");
                        }
                        sub = new TomlTable();
                        target.Set(part, sub);
                    }
                    target = sub;
                    continue;
                }
                Expect('=');
                SkipInlineWhitespace();
                var value = ParseValue();
                SkipInlineWhitespace();
                if (!inline && !AtEnd && Peek() == '#')
                {
                    while (!AtEnd && Peek() != '\n')
                    {
                        Advance();
                    }
                }
                if (!inline && !AtEnd && Peek() is not ('\n' or '\r'))
                {
                    throw new TomlParseException($"Unexpected trailing characters after value for key '{part}'");
                }
                target.Set(part, value);
                return;
            }
        }

        private string ParseKeyPart()
        {
            if (AtEnd)
            {
                throw new TomlParseException("Unexpected end of input while reading a key");
            }
            var c = Peek();
            if (c == '"')
            {
                return ParseBasicString();
            }
            if (c == '\'')
            {
                return ParseLiteralString();
            }
            var start = _pos;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(Peek()) || Peek() is '_' or '-'))
            {
                Advance();
            }
            if (_pos == start)
            {
                throw new TomlParseException($"Invalid key character '{Peek()}'");
            }
            return text[start.._pos];
        }

        private object? ParseValue()
        {
            if (AtEnd)
            {
                throw new TomlParseException("Unexpected end of input while reading a value");
            }
            return Peek() switch
            {
                '"' => ParseBasicString(),
                '\'' => ParseLiteralString(),
                '[' => ParseArray(),
                '{' => ParseInlineTable(),
                _ => ParseAtom(),
            };
        }

        private object ParseAtom()
        {
            var start = _pos;
            while (!AtEnd && Peek() is not (',' or ']' or '}' or '\n' or '\r' or '#'))
            {
                Advance();
            }
            var atom = text[start.._pos].TrimEnd(' ', '\t');
            if (atom == "true")
            {
                return true;
            }
            if (atom == "false")
            {
                return false;
            }
            if (long.TryParse(atom, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
            throw new TomlParseException($"Unsupported TOML value: '{atom}'");
        }

        private object? ParseArray()
        {
            Expect('[');
            var array = new List<object?>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (AtEnd)
                {
                    throw new TomlParseException("Unterminated array");
                }
                if (Peek() == ']')
                {
                    Advance();
                    return array;
                }
                array.Add(ParseValue());
                SkipWhitespaceAndComments();
                if (AtEnd)
                {
                    throw new TomlParseException("Unterminated array");
                }
                if (Peek() == ',')
                {
                    Advance();
                    continue;
                }
                if (Peek() == ']')
                {
                    Advance();
                    return array;
                }
                throw new TomlParseException("Expected ',' or ']' in array");
            }
        }

        private TomlTable ParseInlineTable()
        {
            Expect('{');
            var table = new TomlTable();
            SkipInlineWhitespace();
            if (!AtEnd && Peek() == '}')
            {
                Advance();
                return table;
            }
            while (true)
            {
                SkipInlineWhitespace();
                ParseKeyValue(table, inline: true);
                SkipInlineWhitespace();
                if (AtEnd)
                {
                    throw new TomlParseException("Unterminated inline table");
                }
                if (Peek() == ',')
                {
                    Advance();
                    continue;
                }
                if (Peek() == '}')
                {
                    Advance();
                    return table;
                }
                throw new TomlParseException("Expected ',' or '}' in inline table");
            }
        }

        private string ParseBasicString()
        {
            Expect('"');
            // tolerate the (invalid but harmless) empty "" appearing from writers
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new TomlParseException("Unterminated string");
                }
                var c = Advance();
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (AtEnd)
                {
                    throw new TomlParseException("Unterminated escape sequence");
                }
                var escape = Advance();
                switch (escape)
                {
                    case 'b': sb.Append('\b'); break;
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u': sb.Append(ParseUnicodeEscape(4)); break;
                    case 'U': sb.Append(ParseUnicodeEscape(8)); break;
                    default: throw new TomlParseException($"Invalid escape sequence '\\{escape}'");
                }
            }
        }

        private string ParseUnicodeEscape(int length)
        {
            if (_pos + length > text.Length)
            {
                throw new TomlParseException("Invalid unicode escape");
            }
            var hex = text.Substring(_pos, length);
            _pos += length;
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code))
            {
                throw new TomlParseException($"Invalid unicode escape '\\u{hex}'");
            }
            try
            {
                return char.ConvertFromUtf32(code);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new TomlParseException($"Invalid unicode code point U+{code:X}");
            }
        }

        private string ParseLiteralString()
        {
            Expect('\'');
            var start = _pos;
            while (!AtEnd)
            {
                var c = Advance();
                if (c == '\'')
                {
                    return text[start..(_pos - 1)];
                }
            }
            throw new TomlParseException("Unterminated literal string");
        }

        private void Expect(char expected)
        {
            if (AtEnd || Peek() != expected)
            {
                throw new TomlParseException($"Expected '{expected}'");
            }
            Advance();
        }
    }
}
