// SPDX-License-Identifier: GPL-3.0-only
// Tests for the Log4j XML event parser port (launcher/logs/LogParser.{h,cpp}; guessLevel lives
// in MessageLevel.cs and is covered by the launch tests): complete/partial/mixed event streams,
// the required-attribute errors and the buffer bookkeeping across parseNext/parseAvailable.

using FlintLauncher.Core.Launch;
using Xunit;

namespace FlintLauncher.Tests;

public class LogParserTests
{
    private static string Event(
        string logger = "net.minecraft.server",
        string level = "INFO",
        string timestamp = "1700000000000", // milliseconds since the epoch
        string thread = "Server thread",
        string message = "Starting minecraft server") =>
        $"""
        <log4j:event logger="{logger}" timestamp="{timestamp}" level="{level}" thread="{thread}">
          <log4j:Message>{message}</log4j:Message>
        </log4j:event>
        """;

    // ---------------------------------------------------------------- plain text

    [Fact]
    public void Plain_Text_Lines_Come_Out_As_PlainText()
    {
        var parser = new LogParser();
        // appendLine() concatenates without a separator (C++ semantics), so both appends form
        // a single contiguous plain-text chunk.
        parser.AppendLine("some game output");
        parser.AppendLine("more output");

        var items = parser.ParseAvailable();
        Assert.Single(items);
        Assert.Equal("some game outputmore output", Assert.IsType<LogParser.PlainText>(items[0]).Message);
        Assert.Null(parser.GetError());
    }

    [Fact]
    public void Whitespace_Only_Buffer_Comes_Out_As_PlainText_Without_Trimming()
    {
        var parser = new LogParser();
        parser.AppendLine("   \n\t");
        var items = parser.ParseAvailable();
        var text = Assert.IsType<LogParser.PlainText>(Assert.Single(items));
        Assert.Equal("   \n\t", text.Message);
    }

    [Fact]
    public void Empty_Buffer_Yields_No_Items()
    {
        var parser = new LogParser();
        Assert.Null(parser.ParseNext());
        Assert.Empty(parser.ParseAvailable());
    }

    // ---------------------------------------------------------------- complete events

    [Fact]
    public void Complete_Event_Parses_All_Attributes_And_Message()
    {
        var parser = new LogParser();
        parser.AppendLine(Event());
        var items = parser.ParseAvailable();

        var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(items));
        Assert.Equal("net.minecraft.server", entry.Logger);
        Assert.Equal(MessageLevel.Info, entry.Level);
        Assert.Equal("INFO", entry.LevelText);
        // the timestamp attribute is milliseconds since the epoch (upstream switched from
        // seconds; the Event() default below carries 13 digits accordingly)
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime, entry.Timestamp);
        Assert.Equal("Server thread", entry.Thread);
        Assert.Equal("Starting minecraft server", entry.Message);
    }

    [Fact]
    public void Level_Attribute_Maps_Through_MessageLevel_FromName()
    {
        foreach (var (levelText, expected) in new[]
                 {
                     ("WARN", MessageLevel.Warning), ("ERROR", MessageLevel.Error),
                     ("FATAL", MessageLevel.Fatal), ("DEBUG", MessageLevel.Debug),
                     ("TRACE", MessageLevel.Trace), ("weird", MessageLevel.Unknown),
                 })
        {
            var parser = new LogParser();
            parser.AppendLine(Event(level: levelText));
            var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(parser.ParseAvailable()));
            Assert.Equal(levelText, entry.LevelText);
            Assert.Equal(expected, entry.Level);
        }
    }

    // the C++ entry initializer defaults the level to Info
    [Fact]
    public void Missing_Level_Attribute_Defaults_To_Info()
    {
        var parser = new LogParser();
        parser.AppendLine(
            "<log4j:event logger=\"l\" timestamp=\"1\"><log4j:Message>m</log4j:Message></log4j:event>");
        var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal(MessageLevel.Info, entry.Level);
        Assert.Equal("", entry.LevelText);
    }

    [Fact]
    public void Message_Keeps_Newlines_And_Entities()
    {
        var parser = new LogParser();
        parser.AppendLine(
            "<log4j:event logger=\"l\" level=\"INFO\"><log4j:Message>line1\nline2 &lt;tag&gt;</log4j:Message></log4j:event>");
        var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal("line1\nline2 <tag>", entry.Message);
    }

    [Fact]
    public void Message_Keeps_Whitespace_Inside_The_Element()
    {
        var parser = new LogParser();
        parser.AppendLine(
            "<log4j:event logger=\"l\" level=\"INFO\"><log4j:Message>  spaced  </log4j:Message></log4j:event>");
        var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal("  spaced  ", entry.Message);
    }

    [Fact]
    public void Several_Events_Parse_In_One_Pass()
    {
        var parser = new LogParser();
        parser.AppendLine(Event(logger: "one") + Event(logger: "two", level: "WARN"));
        var items = parser.ParseAvailable();

        Assert.Equal(2, items.Count);
        Assert.Equal("one", Assert.IsType<LogParser.LogEntry>(items[0]).Logger);
        var second = Assert.IsType<LogParser.LogEntry>(items[1]);
        Assert.Equal("two", second.Logger);
        Assert.Equal(MessageLevel.Warning, second.Level);
    }

    [Fact]
    public void Text_Before_An_Event_Is_Split_Out_As_PlainText()
    {
        var parser = new LogParser();
        var eventXml = "<log4j:event logger=\"l\" level=\"INFO\"><log4j:Message>m</log4j:Message></log4j:event>";
        parser.AppendLine("plain text first " + eventXml);

        // C++ semantics: with a COMPLETE event in the buffer, the XML parser silently skips
        // the leading plain text and the event is parsed (the split-to-PlainText path only
        // runs for INCOMPLETE events, where the leading text is emitted first).
        var items = parser.ParseAvailable();
        Assert.Single(items);
        Assert.Equal("l", Assert.IsType<LogParser.LogEntry>(items[0]).Logger);
    }

    [Fact]
    public void Trailing_Text_After_An_Event_Is_Emitted_On_The_Next_Call()
    {
        var parser = new LogParser();
        var eventXml = "<log4j:event logger=\"l\" level=\"INFO\"><log4j:Message>m</log4j:Message></log4j:event>";
        parser.AppendLine(eventXml + "\nplain tail");

        var first = parser.ParseNext();
        var entry = Assert.IsType<LogParser.LogEntry>(first);
        Assert.Equal("l", entry.Logger);
        // the tail stays in the buffer (verbatim, whitespace included)
        var second = parser.ParseNext();
        Assert.Equal("\nplain tail", Assert.IsType<LogParser.PlainText>(second).Message);
        Assert.Null(parser.ParseNext());
    }

    // ---------------------------------------------------------------- partial events

    [Fact]
    public void Incomplete_Event_Is_Returned_As_Partial_Then_Completes()
    {
        var parser = new LogParser();
        var full = "<log4j:event logger=\"l\" level=\"INFO\" thread=\"t\"><log4j:Message>done</log4j:Message></log4j:event>";
        // cut right before the '>' that closes the event start tag: the partial is unterminated
        // XML, so the completeness pre-check keeps it buffered. The "\n" appendLine inserts when
        // joining lands between the attributes and '>' — legal whitespace there.
        var cut = full.IndexOf("><log4j:Message", StringComparison.Ordinal);

        parser.AppendLine(full[..cut]);
        var partial = Assert.IsType<LogParser.Partial>(parser.ParseNext());
        Assert.Equal(full[..cut], partial.Data);
        Assert.Null(parser.GetError());

        parser.AppendLine(full[cut..]);
        var entry = Assert.IsType<LogParser.LogEntry>(parser.ParseNext());
        Assert.Equal("done", entry.Message);
        Assert.Null(parser.ParseNext());
    }

    // a "<log..." fragment that is NOT a log4j event start is plain text
    [Fact]
    public void Non_Log4j_Angled_Text_Is_Plain()
    {
        var parser = new LogParser();
        parser.AppendLine("got <html> in the log <nope>");
        var text = Assert.IsType<LogParser.PlainText>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal("got <html> in the log <nope>", text.Message);
    }

    // plain text prefix before a partial start: the prefix is emitted, the start stays buffered
    [Fact]
    public void Text_Prefix_Before_A_Partial_Event()
    {
        var parser = new LogParser();
        parser.AppendLine("before <log4j:event logger=\"l\" level=\"INFO\"");

        var items = parser.ParseAvailable();
        Assert.Equal("before ", Assert.IsType<LogParser.PlainText>(Assert.Single(items)).Message);

        // the unterminated start remains buffered and comes out as a partial on the next call
        var partial = Assert.IsType<LogParser.Partial>(parser.ParseNext());
        Assert.Equal("<log4j:event logger=\"l\" level=\"INFO\"", partial.Data);

        // completing the partial (appendLine joins with "\n", landing before '>')
        parser.AppendLine("><log4j:Message>x</log4j:Message></log4j:event>");
        var entry = Assert.IsType<LogParser.LogEntry>(parser.ParseNext());
        Assert.Equal("l", entry.Logger);
        Assert.Equal("x", entry.Message);
    }

    // ---------------------------------------------------------------- errors

    [Fact]
    public void Missing_Logger_Attribute_Raises_The_Cpp_Error()
    {
        var parser = new LogParser();
        parser.AppendLine("<log4j:event level=\"INFO\"><log4j:Message>m</log4j:Message></log4j:event>");
        parser.ParseAvailable();
        Assert.Equal("log4j:Event Missing required attribute: logger", parser.GetError()?.ErrMessage);
    }

    [Fact]
    public void Empty_Timestamp_Attribute_Raises_The_Cpp_Error()
    {
        var parser = new LogParser();
        parser.AppendLine("<log4j:event logger=\"l\" timestamp=\"\" level=\"INFO\"><log4j:Message>m</log4j:Message></log4j:event>");
        parser.ParseAvailable();
        Assert.Equal("log4j:Event Missing required attribute: timestamp", parser.GetError()?.ErrMessage);
    }

    [Fact]
    public void Missing_Message_Element_Raises_The_Cpp_Error()
    {
        var parser = new LogParser();
        parser.AppendLine("<log4j:event logger=\"l\" level=\"INFO\"></log4j:event>");
        parser.ParseAvailable();
        Assert.Equal("log4j:Event Missing required attribute: message", parser.GetError()?.ErrMessage);
    }

    // parseAvailable returns an empty list after an error (the buffer does not advance past the
    // erroring content — the C++ consumer discards the parser); a fresh parser reports no error.
    [Fact]
    public void Error_Sticky_Until_A_Fresh_Parser()
    {
        var parser = new LogParser();
        parser.AppendLine("<log4j:event level=\"INFO\"><log4j:Message>m</log4j:Message></log4j:event>");
        Assert.Empty(parser.ParseAvailable());
        Assert.NotNull(parser.GetError());

        var fresh = new LogParser();
        fresh.AppendLine(Event(logger: "good"));
        var items = fresh.ParseAvailable();
        var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(items));
        Assert.Equal("good", entry.Logger);
        Assert.Null(fresh.GetError());
    }

    // a buffer that starts like a log4j event but cannot parse (broken junk) stays buffered as
    // a partial — waiting for more data rather than being dropped as plain text
    [Fact]
    public void Broken_Event_Start_Stays_Partial()
    {
        var parser = new LogParser();
        parser.AppendLine("<log4j:event <log4j:event");
        Assert.Empty(parser.ParseAvailable()); // parseAvailable stops at the partial
        var partial = Assert.IsType<LogParser.Partial>(parser.ParseNext());
        Assert.Equal("<log4j:event <log4j:event", partial.Data);
        Assert.Null(parser.GetError());
    }

    // ---------------------------------------------------------------- timestamp handling

    [Fact]
    public void Timestamp_Is_Milliseconds_Since_Epoch_Utc()
    {
        // log4j's XMLLayout reports the event time in milliseconds (upstream switched from
        // fromSecsSinceEpoch to fromMSecsSinceEpoch)
        var parser = new LogParser();
        parser.AppendLine(Event(timestamp: "0", thread: "t"));
        var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal(DateTime.UnixEpoch, entry.Timestamp);

        parser.AppendLine(Event(timestamp: "1700000000000", thread: "t"));
        var msecs = Assert.IsType<LogParser.LogEntry>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime, msecs.Timestamp);
    }

    [Fact]
    public void Non_Numeric_Timestamp_Falls_Back_To_Epoch()
    {
        // value.trimmed().toLongLong() yields 0 for junk
        var parser = new LogParser();
        parser.AppendLine(Event(timestamp: "junk"));
        var entry = Assert.IsType<LogParser.LogEntry>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal(DateTime.UnixEpoch, entry.Timestamp);
    }

    [Fact]
    public void PlainText_TrailingAngleBracket_IsNotHeldBackAsPartialEvent()
    {
        // "<log4j:event" prefix matching must not swallow a lone '<' at the end of a plain
        // line (the ">w<" emoticon case): the line is emitted as plain text at once
        var parser = new LogParser();
        parser.AppendLine("chat >w<");
        var text = Assert.IsType<LogParser.PlainText>(Assert.Single(parser.ParseAvailable()));
        Assert.Equal("chat >w<", text.Message);

        // a different element whose name merely starts the same is not a log4j event either
        var other = new LogParser();
        other.AppendLine("<log4j:Eventually>later");
        var otherText = Assert.IsType<LogParser.PlainText>(Assert.Single(other.ParseAvailable()));
        Assert.Contains("later", otherText.Message);
    }
}
