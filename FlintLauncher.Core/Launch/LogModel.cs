// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/LogModel.{h,cpp} (headless subset): the ring-buffer log store the
// LaunchTask fills while the game runs.
//
// Deviations from the C++ original:
//  - The QAbstractListModel surface (roles, beginInsertRows/... ) has no headless equivalent;
//    the port keeps the data semantics: a ring buffer of (level, line) entries with max-lines
//    overflow handling, suspend/clear/previousLevel and the plain-text dump.
//  - setLineWrap/setColorLines are pure view options and are not ported.

using System.Text;

namespace FlintLauncher.Core.Launch;

public sealed class LogModel
{
    private (MessageLevel Level, string Line)[] content;

    public int MaxLines
    {
        get;
        set
        {
            // no-op
            if (value == field)
            {
                return;
            }

            // if it all still fits in the buffer, just resize it
            if (firstLine + numLines < field)
            {
                field = value;
                Array.Resize(ref content, value);
                return;
            }

            // otherwise, we need to reorganize the data because it crosses the wrap boundary
            var newContent = new (MessageLevel, string)[value];
            if (numLines <= value)
            {
                // if it all fits in the new buffer, just copy it over
                for (var i = 0; i < numLines; i++)
                {
                    newContent[i] = content[(firstLine + i) % field];
                }
            }
            else
            {
                // if it doesn't fit, part of the data needs to be thrown away (the oldest log messages)
                var lead = numLines - value;
                for (var i = 0; i < value; i++)
                {
                    newContent[i] = content[(firstLine + lead + i) % field];
                }

                numLines = field;
            }

            content = newContent;
            firstLine = 0;
            field = value;
        }
    } = 1000;

    private int firstLine;
    private int numLines;
    private bool suspended;
    public bool StopOnOverflow { get; set; }
    public string OverflowMessage { get;  set; } = "";

    public LogModel()
    {
        content = new (MessageLevel, string)[MaxLines];
    }

    public int RowCount => numLines;

    /// <summary>The (level, line) entries in chronological order.</summary>
    public IReadOnlyList<(MessageLevel Level, string Line)> Entries
    {
        get
        {
            var result = new List<(MessageLevel, string)>(numLines);
            for (var i = 0; i < numLines; i++)
            {
                result.Add(content[(firstLine + i) % MaxLines]);
            }

            return result;
        }
    }

    public bool IsOverFlow => numLines >= MaxLines && StopOnOverflow;

    public void Append(MessageLevel level, string line)
    {
        if (suspended)
        {
            return;
        }

        var lineNum = (firstLine + numLines) % MaxLines;
        // overflow
        if (numLines == MaxLines)
        {
            if (StopOnOverflow)
            {
                // nothing more to do, the buffer is full
                return;
            }

            firstLine = (firstLine + 1) % MaxLines;
            numLines--;
        }
        else if (numLines == MaxLines - 1 && StopOnOverflow)
        {
            level = MessageLevel.Fatal;
            line = OverflowMessage;
        }

        numLines++;
        content[lineNum] = (level, line);
    }

    public void Suspend(bool suspend) => suspended = suspend;

    public bool Suspended => suspended;

    public void Clear()
    {
        firstLine = 0;
        numLines = 0;
    }

    public string ToPlainText()
    {
        var builder = new StringBuilder();
        foreach (var (_, line) in Entries)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Port of LogModel::previousLevel: the level of the newest entry (upstream indexes
    /// the content array without the ring wrap — ported as the newest entry).</summary>
    public MessageLevel PreviousLevel()
    {
        if (numLines > 0)
        {
            return content[(firstLine + numLines - 1) % MaxLines].Level;
        }

        return MessageLevel.Unknown;
    }
}
