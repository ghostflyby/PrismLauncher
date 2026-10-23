// SPDX-License-Identifier: GPL-3.0-only
// A directory that the application has materialized: the factories are the single place
// that creates/verifies existence (the imperative FileOps.EnsureFolderPathExists dance,
// previously scattered across every consumer), and the handle certifies that this step
// HAPPENED — not that it still holds (existence is temporal; consumers keep their IO
// failure handling). Composition over AbsolutePath keeps the TruePath extension vocabulary
// usable at every consumer; conversions are factory-only, never implicit.

using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.IO;

public sealed class MaterializedDirectory
{
    private MaterializedDirectory(AbsolutePath path) => Path = path;

    /// <summary>The anchored absolute path (the interchange form for path-taking APIs).</summary>
    public AbsolutePath Path { get; }

    /// <summary>A lazy BCL view; created on access, never cached.</summary>
    public DirectoryInfo Info => new(Path.Value);

    /// <summary>Whether the directory is still present right now (a temporal probe,
    /// not part of the handle's promise).</summary>
    public bool Exists => Path.ExistsDirectory();

    /// <summary>mkdir -p semantics: creates the directory and all parents, succeeds when it
    /// already exists, and fails (null) when a FILE occupies the path — the FileOps
    /// fault-tolerance contract, surfaced as an absent handle instead of a bool.</summary>
    public static MaterializedDirectory? Ensure(AbsolutePath path) =>
        FileOps.EnsureFolderPathExists(path) ? new MaterializedDirectory(path) : null;

    /// <summary>Adopts an existing directory without creating anything; null when the path
    /// is not a directory right now (read-only consumers).</summary>
    public static MaterializedDirectory? Wrap(AbsolutePath path) =>
        path.ExistsDirectory() ? new MaterializedDirectory(path) : null;

    public override string ToString() => Path.Value;
}
