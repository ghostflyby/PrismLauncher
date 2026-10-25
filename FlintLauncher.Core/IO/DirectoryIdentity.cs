// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using TruePath;

namespace FlintLauncher.Core.IO;

/// <summary>The identity of one directory as a comparable value: equal identities mean
/// the same underlying directory, not merely the same path.
/// <para>Identity follows its directory across renames and moves; deleting and
/// re-creating a directory yields a new identity even at the old path, and identities
/// are never comparable across volumes.</para>
/// <para>On platforms or at moments where identity cannot be observed,
/// <see cref="Of"/> returns <see cref="Unavailable"/> — consumers treat that as
/// "identity unknown" and fall back to path-based behavior instead of guessing.</para></summary>
public readonly record struct DirectoryIdentity(long Inode)
{
    /// <summary>The identity of no directory — the default value, and what
    /// <see cref="Of"/> reports when identity cannot be observed.</summary>
    public static readonly DirectoryIdentity Unavailable = default;

    /// <summary>The identity of whatever <paramref name="path"/> names right now;
    /// <see cref="Unavailable"/> when the path is missing or the platform cannot observe
    /// identity.</summary>
    public static DirectoryIdentity Of(AbsolutePath path)
    {
        var inode = FileTransfer.StatInode(path);
        return inode == 0 ? Unavailable : new DirectoryIdentity(inode);
    }

    /// <summary>Whether this identity was actually observed (false only for
    /// <see cref="Unavailable"/>).</summary>
    public bool IsAvailable => Inode != 0;

    public override string ToString() => IsAvailable ? $"inode {Inode}" : "unavailable";
}
