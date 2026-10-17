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
    private int maxLines = 1000; // C++ m_maxLines default
    private int firstLine;
    private int numLines;
    private bool suspended;
    private bool stopOnOverflow;
    private string overflowMessage = "";

    public LogModel()
    {
        content = new (MessageLevel, string)[maxLines];
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
                result.Add(content[(firstLine + i) % maxLines]);
            }
            return result;
        }
    }

    public bool IsOverFlow => numLines >= maxLines && stopOnOverflow;

    public void Append(MessageLevel level, string line)
    {
        if (suspended)
        {
            return;
        }
        var lineNum = (firstLine + numLines) % maxLines;
        // overflow
        if (numLines == maxLines)
        {
            if (stopOnOverflow)
            {
                // nothing more to do, the buffer is full
                return;
            }
            firstLine = (firstLine + 1) % maxLines;
            numLines--;
        }
        else if (numLines == maxLines - 1 && stopOnOverflow)
        {
            level = MessageLevel.Fatal;
            line = overflowMessage;
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

    public void SetMaxLines(int maxLines)
    {
        // no-op
        if (maxLines == this.maxLines)
        {
            return;
        }
        // if it all still fits in the buffer, just resize it
        if (firstLine + numLines < this.maxLines)
        {
            this.maxLines = maxLines;
            Array.Resize(ref content, maxLines);
            return;
        }
        // otherwise, we need to reorganize the data because it crosses the wrap boundary
        var newContent = new (MessageLevel, string)[maxLines];
        if (numLines <= maxLines)
        {
            // if it all fits in the new buffer, just copy it over
            for (var i = 0; i < numLines; i++)
            {
                newContent[i] = content[(firstLine + i) % this.maxLines];
            }
        }
        else
        {
            // if it doesn't fit, part of the data needs to be thrown away (the oldest log messages)
            var lead = numLines - maxLines;
            for (var i = 0; i < maxLines; i++)
            {
                newContent[i] = content[(firstLine + lead + i) % this.maxLines];
            }
            numLines = this.maxLines;
        }
        content = newContent;
        firstLine = 0;
        this.maxLines = maxLines;
    }

    public int GetMaxLines() => maxLines;

    public void SetStopOnOverflow(bool stop) => stopOnOverflow = stop;

    public void SetOverflowMessage(string overflowMessage) => this.overflowMessage = overflowMessage;

    /// <summary>Port of LogModel::previousLevel: the level of the newest entry (upstream indexes
    /// the content array without the ring wrap — ported as the newest entry).</summary>
    public MessageLevel PreviousLevel()
    {
        if (numLines > 0)
        {
            return content[(firstLine + numLines - 1) % maxLines].Level;
        }
        return MessageLevel.Unknown;
    }
}
