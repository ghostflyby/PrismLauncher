// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The unified five-root test fixture: stamps a fresh temp directory tree and
// hands out the resulting FlintPaths, replacing the bare temp-dir fallbacks the
// cleanup removed the last production excuse for. Two shapes:
//  - UnderHome(): one temp home, five roots as its config/data/store/cache/state children
//    (the home layout — what a host started with --home-dir sees);
//  - IndependentRoots(): five separate temp roots (the precise-flag shape — catches any
//    cross-root assumption the zero-implication discipline forbids).

using System.Runtime.CompilerServices;
using FlintLauncher.Core;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Tests;

public sealed class TestFlintPaths : IDisposable
{
    private readonly List<AbsolutePath> _created = [];

    private TestFlintPaths(FlintPaths paths, AbsolutePath? homeRoot)
    {
        Paths = paths;
        HomeRoot = homeRoot;
    }

    public FlintPaths Paths { get; }

    /// <summary>The temp home (UnderHome shape only; null for independent roots).</summary>
    public AbsolutePath? HomeRoot { get; }

    /// <summary>Convenience for the data root — the fixture shape most suites assert on.</summary>
    public AbsolutePath DataDir => Paths.DataRoot;

    /// <summary>One temp home; the five roots are created under it before the host starts
    /// (so fixture seeding can write below them). Pass <see cref="HomeRoot"/> as
    /// --home-dir when hosting the real app.</summary>
    public static TestFlintPaths UnderHome([CallerMemberName] string stamp = "")
    {
        var home = NewTempDir(stamp);
        var paths = FlintPaths.FromHomeDir(home);
        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot, paths.CacheRoot, paths.StateRoot,
                 })
        {
            root.CreateDirectory();
        }

        return new TestFlintPaths(paths, home) { _created = { home } };
    }

    /// <summary>Five independent temp roots (the precise-flag shape, zero-implication
    /// discipline).</summary>
    public static TestFlintPaths IndependentRoots([CallerMemberName] string stamp = "")
    {
        AbsolutePath configRoot = NewTempDir(stamp);
        AbsolutePath dataRoot = NewTempDir(stamp);
        AbsolutePath storeRoot = NewTempDir(stamp);
        AbsolutePath cacheRoot = NewTempDir(stamp);
        AbsolutePath stateRoot = NewTempDir(stamp);
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = dataRoot,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };
        var fixture = new TestFlintPaths(paths, null);
        fixture._created.AddRange(
        [
            paths.ConfigRoot, paths.DataRoot, paths.StoreRoot, paths.CacheRoot, paths.StateRoot,
        ]);
        return fixture;
    }

    private static AbsolutePath NewTempDir(string stamp) =>
        Temporary.SystemTempDirectory() /
        ($"flint-test-{stamp}-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var dir in _created)
        {
            try
            {
                dir.DeleteDirectoryRecursively();
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
