// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/LogModel.{h,cpp} (headless subset): the ring-buffer log store the
// LaunchTask fills while the game runs.
//
// Deviations from the C++ original:
//  - The QAbstractListModel surface (roles, beginInsertRows/... ) has no headless equivalent;
//    the port keeps the data semantics: a ring buffer of (level, line) entries with max-lines
//    overflow handling, suspend/clear/previousLevel and the plain-text dump.
//  - setLineWrap/setColorLines are pure view options and are not ported.
namespace FlintLauncher.Core.Launch;

public sealed class LogModel
{
    private (MessageLevel Level, string Line)[] _content;
    private int _maxLines = 1000; // C++ m_maxLines default
    private int _firstLine;
    private int _numLines;
    private bool _suspended;
    private bool _stopOnOverflow;
    private string _overflowMessage = "";

    public LogModel()
    {
        _content = new (MessageLevel, string)[_maxLines];
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
                result.Add(_content[(_firstLine + i) % _maxLines]);
            }
            return result;
        }
    }

    public bool IsOverFlow => _numLines >= _maxLines && _stopOnOverflow;

    public void Append(MessageLevel level, string line)
    {
        if (_suspended)
        {
            return;
        }
        var lineNum = (_firstLine + _numLines) % _maxLines;
        // overflow
        if (_numLines == _maxLines)
        {
            if (_stopOnOverflow)
            {
                // nothing more to do, the buffer is full
                return;
            }
            _firstLine = (_firstLine + 1) % _maxLines;
            _numLines--;
        }
        else if (_numLines == _maxLines - 1 && _stopOnOverflow)
        {
            level = MessageLevel.Fatal;
            line = _overflowMessage;
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
        var builder = new System.Text.StringBuilder();
        foreach (var (_, line) in Entries)
        {
            builder.Append(line).Append('\n');
        }
        return builder.ToString();
    }

    public void SetMaxLines(int maxLines)
    {
        // no-op
        if (maxLines == _maxLines)
        {
            return;
        }
        // if it all still fits in the buffer, just resize it
        if (_firstLine + _numLines < _maxLines)
        {
            _maxLines = maxLines;
            Array.Resize(ref _content, maxLines);
            return;
        }
        // otherwise, we need to reorganize the data because it crosses the wrap boundary
        var newContent = new (MessageLevel, string)[maxLines];
        if (_numLines <= maxLines)
        {
            // if it all fits in the new buffer, just copy it over
            for (var i = 0; i < _numLines; i++)
            {
                newContent[i] = _content[(_firstLine + i) % _maxLines];
            }
        }
        else
        {
            // if it doesn't fit, part of the data needs to be thrown away (the oldest log messages)
            var lead = _numLines - maxLines;
            for (var i = 0; i < maxLines; i++)
            {
                newContent[i] = _content[(_firstLine + lead + i) % _maxLines];
            }
            _numLines = _maxLines;
        }
        _content = newContent;
        _firstLine = 0;
        _maxLines = maxLines;
    }

    public int GetMaxLines() => _maxLines;

    public void SetStopOnOverflow(bool stop) => _stopOnOverflow = stop;

    public void SetOverflowMessage(string overflowMessage) => _overflowMessage = overflowMessage;

    /// <summary>Port of LogModel::previousLevel: the level of the newest entry (upstream indexes
    /// the content array without the ring wrap — ported as the newest entry).</summary>
    public MessageLevel PreviousLevel()
    {
        if (_numLines > 0)
        {
            return _content[(_firstLine + _numLines - 1) % _maxLines].Level;
        }
        return MessageLevel.Unknown;
    }
}
