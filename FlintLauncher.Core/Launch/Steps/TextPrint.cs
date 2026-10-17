// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/steps/TextPrint.{h,cpp}: print static text into the launch log.

namespace FlintLauncher.Core.Launch.Steps;

public sealed class TextPrint : LaunchStep
{
    private readonly List<string> _lines;
    private readonly MessageLevel _level;

    public TextPrint(LaunchTask parent, IReadOnlyList<string> lines, MessageLevel level) : base(parent)
    {
        _lines = [.. lines];
        _level = level;
    }

    public TextPrint(LaunchTask parent, string line, MessageLevel level) : base(parent)
    {
        _lines = [line];
        _level = level;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitLogLines(_lines, _level);
        EmitSucceeded();
        return Task.CompletedTask;
    }

    public override bool CanAbort() => true;

    public override bool AbortStep()
    {
        EmitAborted();
        return true;
    }
}
