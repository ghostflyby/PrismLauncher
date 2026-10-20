// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/logs/LogParser.{h,cpp}: a streaming parser for log4j XML event logs (the
// XMLLayout appender output Minecraft can produce) mixed with plain text lines. guessLevel()
// already lives in Launch/MessageLevel.cs; this file adds the event parser itself.
//
// Deviations:
//  - QXmlStreamReader runs with namespace processing off, so it tolerates the undeclared
//    "log4j:" prefix of streamed events; System.Xml.XmlReader does not. The port declares the
//    buffer's root prefix (normally "log4j") through an XmlParserContext instead and matches
//    qualified names case-insensitively like the C++.
//  - The consumed-character bookkeeping uses IXmlLineInfo (the '<' of the event's end tag plus
//    its text length) in place of QXmlStreamReader::characterOffset().
//  - QXmlStreamReader::Error codes are collapsed into the error message string only.

using System.Globalization;
using System.Text;
using System.Xml;

namespace FlintLauncher.Core.Launch;

/// <summary>Port of LogParser: buffer + partial-event tracking + the log4j event parser.</summary>
public sealed class LogParser
{
    /// <summary>Port of LogParser::LogEntry (timestamp null = the C++ invalid QDateTime).</summary>
    public sealed record LogEntry(
        string Logger,
        MessageLevel Level,
        string LevelText,
        DateTime? Timestamp,
        string Thread,
        string Message) : ParsedItem;

    /// <summary>Port of LogParser::PlainText.</summary>
    public sealed record PlainText(string Message) : ParsedItem;

    /// <summary>Port of LogParser::Partial (an event split across appendLine() calls).</summary>
    public sealed record Partial(string Data) : ParsedItem;

    /// <summary>Port of LogParser::Error (the reader error code is folded into the message).</summary>
    public sealed record ParseError(string ErrMessage);

    /// <summary>Port of the ParsedItem variant.</summary>
    public abstract record ParsedItem;

    private const string TargetStart = "<log4j:event";

    private string _buffer = "";
    private string _partialData = "";
    private ParseError? _error;

    /// <summary>Port of appendLine(): completes a pending partial with a newline separator, then
    /// appends the incoming data.</summary>
    public void AppendLine(string data)
    {
        if (_partialData.Length > 0)
        {
            _buffer = _partialData;
            _buffer += "\n";
            _partialData = "";
        }

        _buffer += data;
    }

    /// <summary>Port of getError().</summary>
    public ParseError? GetError() => _error;

    /// <summary>Port of parseNext(): null when the buffer is exhausted.</summary>
    public ParsedItem? ParseNext()
    {
        _error = null;

        if (_buffer.Length == 0)
        {
            return null;
        }

        if (_buffer.Trim().Length == 0)
        {
            var text = _buffer;
            _buffer = "";
            return new PlainText(text);
        }

        // check if we have a full xml log4j event
        var isCompleteLog4J = false;
        try
        {
            using var parser = CreateReader(_buffer);
            if (ReadNextStartElement(parser) && MatchesEvent(parser.Name))
            {
                var depth = 1;
                var eod = false;
                while (depth > 0 && !eod)
                {
                    if (!parser.Read())
                    {
                        break; // EOF: the event is incomplete
                    }

                    switch (parser.NodeType)
                    {
                        case XmlNodeType.Element:
                            depth += 1;
                            break;
                        case XmlNodeType.EndElement:
                            depth -= 1;
                            break;
                        // (System.Xml has no EndDocument token: Read() returns false at the end,
                        // which the EOF break above treats as an incomplete event.)
                    }
                }

                isCompleteLog4J = depth == 0 && !eod;
            }
        }
        catch (XmlException)
        {
            isCompleteLog4J = false;
        }

        if (isCompleteLog4J)
        {
            return ParseLog4J();
        }

        if (IsPotentialLog4JStart(_buffer))
        {
            _partialData = _buffer;
            return new Partial(_buffer);
        }

        var start = 0;
        while (start < _buffer.Length)
        {
            var pos = _buffer.IndexOf('<', start);
            if (pos == -1)
            {
                break;
            }

            var slice = _buffer[pos..];
            if (IsPotentialLog4JStart(slice))
            {
                if (pos > 0)
                {
                    var text = _buffer[..pos];
                    _buffer = _buffer[pos..];
                    if (text.Trim().Length > 0)
                    {
                        return new PlainText(text);
                    }
                }

                _partialData = _buffer;
                return new Partial(_buffer);
            }

            start = pos + 1;
        }

        // no log4j found, all plain text
        var allText = _buffer;
        _buffer = "";
        return new PlainText(allText);
    }

    /// <summary>Port of parseAvailable(): all fully parseable items; stops at a partial event;
    /// returns an empty list when a parse error occurred.</summary>
    public List<ParsedItem> ParseAvailable()
    {
        var items = new List<ParsedItem>();
        var doNext = true;
        while (doNext)
        {
            var item = ParseNext();
            if (_error is not null)
            {
                return [];
            }

            if (item is not null)
            {
                if (item is Partial)
                {
                    break;
                }

                items.Add(item);
            }
            else
            {
                doNext = false;
            }
        }

        return items;
    }

    // ---------------------------------------------------------------- internals

    private ParsedItem? ParseLog4J()
    {
        using var parser = CreateReader(_buffer);
        try
        {
            if (!ReadNextStartElement(parser) || !MatchesEvent(parser.Name))
            {
                // unreachable: already verified this was a complete log4j:Event
                return null;
            }

            var entry = ParseAttributes(parser);
            if (entry is null)
            {
                return null; // _error already set (missing timestamp/logger)
            }

            var foundMessage = false;
            var depth = 1;

            while (true)
            {
                if (!parser.Read())
                {
                    return null; // EOF without the closing event element
                }

                switch (parser.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        depth += 1;
                        if (MatchesMessage(parser.Name))
                        {
                            var message = new StringBuilder();
                            var messageComplete = false;
                            while (!messageComplete)
                            {
                                if (!parser.Read())
                                {
                                    return null; // parse fail (EndDocument/EOF)
                                }

                                switch (parser.NodeType)
                                {
                                    case XmlNodeType.Text:
                                    case XmlNodeType.CDATA:
                                    case XmlNodeType.Whitespace:
                                    case XmlNodeType.SignificantWhitespace:
                                        message.Append(parser.Value);
                                        break;
                                    case XmlNodeType.EndElement:
                                        if (MatchesMessage(parser.Name))
                                        {
                                            messageComplete = true;
                                        }

                                        break;
                                    // (no EndDocument token in System.Xml; EOF returns null above)
                                }
                            }

                            entry = entry with { Message = message.ToString() };
                            foundMessage = true;
                            depth -= 1;
                        }

                        break;
                    }
                    case XmlNodeType.EndElement:
                    {
                        depth -= 1;
                        if (depth == 0 && MatchesEvent(parser.Name))
                        {
                            if (foundMessage)
                            {
                                var consumed = ConsumedOffset(parser, _buffer);
                                if (consumed > 0 && consumed <= _buffer.Length)
                                {
                                    _buffer = _buffer[consumed..];
                                    // potential whitespace preserved for next item
                                }

                                _error = null; // clearError
                                return entry;  // entryReady
                            }

                            _error = new ParseError("log4j:Event Missing required attribute: message");
                            return null;
                        }

                        break;
                    }
                    // (no EndDocument token in System.Xml; Read() returning false ends the loop)
                }
            }
        }
        catch (XmlException e)
        {
            _error = new ParseError(e.Message);
            return null;
        }
    }

    /// <summary>Port of parseAttributes(): collects logger/timestamp/level/thread. The logger
    /// attribute is required; level defaults to Info (like the C++ entry initializer). Returns
    /// null (with _error set) on a missing required attribute.</summary>
    private LogEntry? ParseAttributes(XmlReader parser)
    {
        var entry = new LogEntry("", MessageLevel.Info, "", null, "", "");
        var logger = "";
        if (parser.MoveToFirstAttribute())
        {
            do
            {
                var name = parser.Name;
                var value = parser.Value.Trim();
                switch (name)
                {
                    case "logger":
                        logger = value;
                        break;
                    case "timestamp":
                        if (value.Length == 0)
                        {
                            _error = new ParseError("log4j:Event Missing required attribute: timestamp");
                            return null;
                        }

                        entry = entry with { Timestamp = FromMSecsSinceEpoch(value) };
                        break;
                    case "level":
                        entry = entry with { LevelText = value, Level = MessageLevel.Launcher.FromName(value) };
                        break;
                    case "thread":
                        entry = entry with { Thread = value };
                        break;
                }
            } while (parser.MoveToNextAttribute());
        }

        parser.MoveToElement();
        if (logger.Length == 0)
        {
            _error = new ParseError("log4j:Event Missing required attribute: logger");
            return null;
        }

        return entry with { Logger = logger };
    }

    /// <summary>Port of the timestamp attribute read: log4j's XMLLayout reports the event
    /// time in milliseconds (upstream switched from fromSecsSinceEpoch).</summary>
    private static DateTime? FromMSecsSinceEpoch(string value)
    {
        // value.trimmed().toLongLong() — 0 on failure
        if (!long.TryParse(value, NumberStyles.AllowLeadingWhite | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var msecs))
        {
            msecs = 0;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(msecs).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // the C++ invalid QDateTime
        }
    }

    /// <summary>
    /// Port of isPotentialLog4JStart. Data reaches the parser one whole line at a time and
    /// log4j's XMLLayout never splits the "&lt;log4j:Event" start tag across lines, so a slice
    /// that is merely a *prefix* of the tag can never be completed by later input: accepting
    /// such a prefix used to tear a lone '&lt;' (as in the emoticon "&gt;w&lt;") off the end of
    /// its line and hold it back until the next line arrived. After the full tag name, only a
    /// name terminator keeps it one of ours ("&lt;log4j:Eventually" is a different element).
    /// </summary>
    private static bool IsPotentialLog4JStart(string buffer)
    {
        if (!buffer.StartsWith(TargetStart, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (buffer.Length == TargetStart.Length)
        {
            return true; // the name is complete, whatever follows it is on the next line
        }

        var next = buffer[TargetStart.Length];
        return char.IsWhiteSpace(next) || next == '>' || next == '/';
    }

    private static bool MatchesEvent(string name) => name.Equals("log4j:Event", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesMessage(string name) => name.Equals("log4j:Message", StringComparison.OrdinalIgnoreCase);

    /// <summary>readNextStartElement(): advance to the next StartElement; false at EOF/error.</summary>
    private static bool ReadNextStartElement(XmlReader parser)
    {
        while (parser.Read())
        {
            if (parser.NodeType == XmlNodeType.Element)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A namespace-tolerant reader over the buffer: QXmlStreamReader runs namespace-unaware, so
    /// the streamed "log4j:" prefix needs to be pre-declared for System.Xml. The prefix is taken
    /// from the buffer's first element start tag.
    /// </summary>
    private static XmlReader CreateReader(string data)
    {
        var settings = new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        };
        var prefix = RootPrefix(data);
        XmlParserContext context;
        if (prefix.Length > 0)
        {
            var nsManager = new XmlNamespaceManager(new NameTable());
            nsManager.AddNamespace(prefix, "http://jakarta.apache.org/log4j/");
            context = new XmlParserContext(null, nsManager, "", XmlSpace.None);
        }
        else
        {
            context = new XmlParserContext(null, null, "", XmlSpace.None);
        }

        return XmlReader.Create(new StringReader(data), settings, context);
    }

    /// <summary>The prefix of the buffer's first element start tag ("" when unprefixed).</summary>
    private static string RootPrefix(string data)
    {
        var lt = data.IndexOf('<');
        if (lt < 0 || lt + 1 >= data.Length)
        {
            return "";
        }

        var i = lt + 1;
        if (!char.IsAsciiLetter(data[i]))
        {
            return "";
        }

        var nameStart = i;
        while (i < data.Length && (char.IsAsciiLetterOrDigit(data[i]) || data[i] == '_' || data[i] == '.' || data[i] == '-'))
        {
            i++;
        }

        if (i < data.Length && data[i] == ':')
        {
            return data[nameStart..i];
        }

        return "";
    }

    /// <summary>The number of buffer characters consumed through the end tag the reader is
    /// currently on (stand-in for QXmlStreamReader::characterOffset()).</summary>
    private static int ConsumedOffset(XmlReader parser, string buffer)
    {
        var lineInfo = (IXmlLineInfo)parser;
        var absolute = AbsoluteOffset(buffer, lineInfo.LineNumber, lineInfo.LinePosition);
        // The reported position is somewhere inside the end tag; find its '<' and its '>'.
        var tagStart = buffer.LastIndexOf('<', Math.Max(0, Math.Min(absolute, buffer.Length - 1)));
        if (tagStart < 0)
        {
            return buffer.Length;
        }

        var gt = buffer.IndexOf('>', tagStart);
        return gt < 0 ? buffer.Length : gt + 1;
    }

    private static int AbsoluteOffset(string buffer, int line, int position)
    {
        if (line <= 1)
        {
            return position - 1;
        }

        var index = 0;
        var currentLine = 1;
        while (currentLine < line && index < buffer.Length)
        {
            var newline = buffer.IndexOf('\n', index);
            if (newline < 0)
            {
                return position - 1;
            }

            index = newline + 1;
            currentLine++;
        }

        return index + position - 1;
    }
}
