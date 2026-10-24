// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
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
    private (MessageLevel Level, string Line)[] _content;

    public int MaxLines
    {
        get;
        init
        {
            // no-op
            if (value == field)
            {
                return;
            }

            // if it all still fits in the buffer, just resize it
            if (_firstLine + _numLines < field)
            {
                field = value;
                Array.Resize(ref _content, value);
                return;
            }

            // otherwise, we need to reorganize the data because it crosses the wrap boundary
            var newContent = new (MessageLevel, string)[value];
            if (_numLines <= value)
            {
                // if it all fits in the new buffer, just copy it over
                for (var i = 0; i < _numLines; i++)
                {
                    newContent[i] = _content[(_firstLine + i) % field];
                }
            }
            else
            {
                // if it doesn't fit, part of the data needs to be thrown away (the oldest log messages)
                var lead = _numLines - value;
                for (var i = 0; i < value; i++)
                {
                    newContent[i] = _content[(_firstLine + lead + i) % field];
                }

                _numLines = field;
            }

            _content = newContent;
            _firstLine = 0;
            field = value;
        }
    } = 1000;

    private int _firstLine;
    private int _numLines;
    private bool _suspended;
    public bool StopOnOverflow { get; init; }
    public string OverflowMessage { get; set; } = "";

    public LogModel()
    {
        _content = new (MessageLevel, string)[MaxLines];
    }

    public int RowCount => _numLines;

    /// <summary>The (level, line) entries in chronological order.</summary>
    public IReadOnlyList<(MessageLevel Level, string Line)> Entries
    {
        get
        {
            var result = new List<(MessageLevel, string)>(_numLines);
            for (var i = 0; i < _numLines; i++)
            {
                result.Add(_content[(_firstLine + i) % MaxLines]);
            }

            return result;
        }
    }

    public bool IsOverFlow => _numLines >= MaxLines && StopOnOverflow;

    public void Append(MessageLevel level, string line)
    {
        if (_suspended)
        {
            return;
        }

        var lineNum = (_firstLine + _numLines) % MaxLines;
        // overflow
        if (_numLines == MaxLines)
        {
            if (StopOnOverflow)
            {
                // nothing more to do, the buffer is full
                return;
            }

            _firstLine = (_firstLine + 1) % MaxLines;
            _numLines--;
        }
        else if (_numLines == MaxLines - 1 && StopOnOverflow)
        {
            level = MessageLevel.Fatal;
            line = OverflowMessage;
        }

        _numLines++;
        _content[lineNum] = (level, line);
    }

    public void Suspend(bool suspend) => _suspended = suspend;

    public bool Suspended => _suspended;

    public void Clear()
    {
        _firstLine = 0;
        _numLines = 0;
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
        if (_numLines > 0)
        {
            return _content[(_firstLine + _numLines - 1) % MaxLines].Level;
        }

        return MessageLevel.Unknown;
    }
}
