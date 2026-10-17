// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/ProblemProvider.h.

namespace FlintLauncher.Core.Minecraft;

public enum ProblemSeverity
{
    None,
    Warning,
    Error,
}

public readonly struct PatchProblem
{
    public PatchProblem(ProblemSeverity severity, string description)
    {
        Severity = severity;
        Description = description;
    }

    public ProblemSeverity Severity { get; }

    public string Description { get; }
}

public abstract class ProblemProvider
{
    public abstract IReadOnlyList<PatchProblem> GetProblems();

    public abstract ProblemSeverity GetProblemSeverity();
}

public class ProblemContainer : ProblemProvider
{
    private readonly List<PatchProblem> _problems = [];
    private ProblemSeverity problemSeverity = ProblemSeverity.None;

    public override IReadOnlyList<PatchProblem> GetProblems() => _problems;

    public override ProblemSeverity GetProblemSeverity() => problemSeverity;

    public void AddProblem(ProblemSeverity severity, string description)
    {
        if (severity > problemSeverity)
        {
            problemSeverity = severity;
        }

        _problems.Add(new PatchProblem(severity, description));
    }
}
