// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using TruePath;

namespace FlintLauncher.Web.Services;

/// <summary>The migration source surface of one installation: the Prism-family data
/// directories the detector currently finds, and the membership test every planning
/// endpoint applies to a user-supplied source path. The read/planning primitives (the
/// plan view, the dry-run preview) enumerate a directory's full inventory — a raw path
/// there is an arbitrary-directory probe primitive, so they accept only detected
/// candidates; the state-changing operations (import, decline) keep their own gates.</summary>
public sealed class PrismSourceCatalog
{
    private readonly AbsolutePath _targetDataDir;
    private readonly MigrationDecisionStore _decisions;
    private readonly AbsolutePath _configFile;
    private readonly IReadOnlyList<AbsolutePath> _parents;

    public PrismSourceCatalog(FlintPaths paths, MigrationDecisionStore decisions)
        : this(paths, decisions, PrismSourceDetector.DefaultParents())
    {
    }

    /// <summary>The test seam: detection over explicit parent directories (a sandbox root)
    /// instead of the per-OS defaults.</summary>
    public PrismSourceCatalog(FlintPaths paths, MigrationDecisionStore decisions,
        IReadOnlyList<AbsolutePath> parents)
    {
        _targetDataDir = paths.DataDir;
        _decisions = decisions;
        _configFile = paths.ConfigFile;
        _parents = parents;
    }

    public IReadOnlyList<PrismSourceDetector.PrismSourceCandidate> Detect() =>
        PrismSourceDetector.Detect(_targetDataDir, _parents, _decisions, _configFile);

    /// <summary>Whether <paramref name="source"/> names one of the detected candidates.
    /// Paths compare full and case-insensitively on Windows (the same normalization the
    /// watch layer applies to its paths).</summary>
    public bool IsKnownSource(string source)
    {
        var full = Path.GetFullPath(source);
        foreach (var candidate in Detect())
        {
            if (string.Equals(candidate.Path.Value, full, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
