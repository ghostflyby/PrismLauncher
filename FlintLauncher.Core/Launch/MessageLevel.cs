// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/MessageLevel.{h,cpp} plus the LogParser::guessLevel level-detection
// heuristics (launcher/logs/LogParser.cpp) the launch pipeline uses to tag game output.
//
// Deviations from the C++ original:
//  - The C++ MessageLevel is an EnumWrapper<MessageLevel, MessageLevelValue>; the C# port is a
//    plain enum with the same values in the same order. fromName() maps through the same
//    name table (case-insensitive, as the C++ upper-cases the input first).
//  - fromQtMsgType() has no Qt logging layer to map from and is not ported.
//  - guessLevel() is the port of LogParser::guessLevel (the launcher log has no XML parser of
//    its own; the full log4j XML event parser of LogParser is not ported — see LaunchTask).

using System.Text.RegularExpressions;

namespace FlintLauncher.Core.Launch;

/// <summary>Port of MessageLevelValue/MessageLevel: what level a log message is.</summary>
public enum MessageLevel : byte
{
    /// <summary>No idea what this is or where it came from</summary>
    Unknown,

    /// <summary>Undetermined stdout messages</summary>
    StdOut,

    /// <summary>Undetermined stderr messages</summary>
    StdErr,

    /// <summary>Launcher Messages</summary>
    Launcher,

    /// <summary>Trace Messages</summary>
    Trace,

    /// <summary>Debug Messages</summary>
    Debug,

    /// <summary>Info Messages</summary>
    Info,

    /// <summary>Standard Messages</summary>
    Message,

    /// <summary>Warnings</summary>
    Warning,

    /// <summary>Errors</summary>
    Error,

    /// <summary>Fatal Errors</summary>
    Fatal,
}

public static class MessageLevelExtensions
{
    private static readonly (MessageLevel Level, string Name)[] Mapping =
    [
        (MessageLevel.Unknown, "UNKNOWN"),
        (MessageLevel.Launcher, "LAUNCHER"),
        (MessageLevel.Trace, "TRACE"),
        (MessageLevel.Debug, "DEBUG"),
        (MessageLevel.Info, "INFO"),
        (MessageLevel.Message, "MESSAGE"),
        (MessageLevel.Warning, "WARNING"),
        (MessageLevel.Warning, "WARN"),
        (MessageLevel.Error, "ERROR"),
        (MessageLevel.Error, "CRITICAL"),
        (MessageLevel.Fatal, "FATAL"),
    ];

    /// <summary>Port of MessageLevel::fromName (case-insensitive; unknown names are invalid).</summary>
    public static MessageLevel FromName(this MessageLevel _, string type)
    {
        var upper = type.ToUpperInvariant();
        foreach (var (level, name) in Mapping)
        {
            if (name == upper)
            {
                return level;
            }
        }

        return MessageLevel.Unknown;
    }

    /// <summary>Port of MessageLevel::takeFromLine: level from a "!![LEVEL]!" prefix; the
    /// prefix is removed from the line when it was successful.</summary>
    public static MessageLevel TakeFromLine(this MessageLevel _, ref string line)
    {
        // Level prefix
        var endmark = line.IndexOf("]!", StringComparison.Ordinal);
        if (line.StartsWith("!![", StringComparison.Ordinal) && endmark != -1)
        {
            var level = MessageLevel.Launcher.FromName(line[..endmark][3..]);
            line = line[(endmark + 2)..];
            return level;
        }

        return MessageLevel.Unknown;
    }

    /// <summary>Port of MessageLevel::takeFromLauncherLine: level from a "123: LEVEL: rest"
    /// launcher log line; the prefix is removed from the line when it was successful.</summary>
    public static MessageLevel TakeFromLauncherLine(this MessageLevel _, ref string line)
    {
        // Level prefix
        var startMark = 0;
        while (startMark < line.Length && (char.IsAsciiDigit(line[startMark]) || char.IsWhiteSpace(line[startMark]) || line[startMark] == '.'))
        {
            startMark++;
        }

        var endmark = line.IndexOf(':', StringComparison.Ordinal);
        if (startMark < line.Length && endmark != -1)
        {
            var level = MessageLevel.Launcher.FromName(line[..endmark][startMark..]);
            line = line[(endmark + 2)..];
            return level;
        }

        return MessageLevel.Unknown;
    }

    /// <summary>
    /// Port of LogParser::guessLevel: heuristics for tagging a game log line when the XML
    /// parser did not produce a level. <paramref name="previous"/> is the level of the previous
    /// model entry (continuation lines inherit it).
    /// </summary>
    public static MessageLevel GuessLevel(this MessageLevel _, string line, MessageLevel previous)
    {
        const string lineWithLevel = "^\\[[^\\]]+\\] \\[[^/]+/(?<level>[^\\]]+)\\]";
        var match = Regex.Match(line, lineWithLevel);
        if (match.Success)
        {
            // New style logs from log4j
            var levelStr = match.Groups["level"].Value;
            return MessageLevel.Launcher.FromName(levelStr);
        }

        // Old style forge logs
        if (line.Contains("[INFO]", StringComparison.Ordinal) || line.Contains("[CONFIG]", StringComparison.Ordinal) || line.Contains("[FINE]", StringComparison.Ordinal) || line.Contains("[FINER]", StringComparison.Ordinal) ||
            line.Contains("[FINEST]", StringComparison.Ordinal))
        {
            return MessageLevel.Info;
        }

        if (line.Contains("[SEVERE]", StringComparison.Ordinal) || line.Contains("[STDERR]", StringComparison.Ordinal))
        {
            return MessageLevel.Error;
        }

        if (line.Contains("[WARNING]", StringComparison.Ordinal))
        {
            return MessageLevel.Warning;
        }

        if (line.Contains("[DEBUG]", StringComparison.Ordinal))
        {
            return MessageLevel.Debug;
        }

        if (line.Contains("Exception: ") || line.Contains("Throwable: "))
        {
            return MessageLevel.Error;
        }

        if (line.StartsWith("Caused by: ", StringComparison.Ordinal) || line.StartsWith("Exception in thread", StringComparison.Ordinal))
        {
            return MessageLevel.Error;
        }

        if (line.Contains("overwriting existing"))
        {
            return MessageLevel.Fatal;
        }

        if (line.StartsWith('\t') || line.StartsWith(' '))
        {
            return previous;
        }

        return MessageLevel.Unknown;
    }
}
