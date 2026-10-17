// SPDX-License-Identifier: GPL-3.0-only
// The unified five-root test fixture (GLOBAL §1): stamps a fresh temp directory tree and
// hands out the resulting FlintPaths, replacing the bare temp-dir fallbacks the E6 cleanup
// removed the last production excuse for. Two shapes:
//  - UnderHome(): one temp home, five roots as its config/data/store/cache/state children
//    (the G1 home layout — what a host started with --home-dir sees);
//  - IndependentRoots(): five separate temp roots (the precise-flag shape — catches any
//    cross-root assumption the zero-implication discipline forbids).

using System.Runtime.CompilerServices;
using FlintLauncher.Core;

namespace FlintLauncher.Tests;

public sealed class TestFlintPaths : IDisposable
{
    private readonly List<string> _created = [];

    private TestFlintPaths(FlintPaths paths, string? homeRoot)
    {
        Paths = paths;
        HomeRoot = homeRoot;
    }

    public FlintPaths Paths { get; }

    /// <summary>The temp home (UnderHome shape only; null for independent roots).</summary>
    public string? HomeRoot { get; }

    /// <summary>Convenience for the data root — the fixture shape most suites assert on.</summary>
    public string DataDir => Paths.DataRoot;

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
            Directory.CreateDirectory(root);
        }

        return new TestFlintPaths(paths, home) { _created = { home } };
    }

    /// <summary>Five independent temp roots (the precise-flag shape, G1 zero-implication
    /// discipline).</summary>
    public static TestFlintPaths IndependentRoots([CallerMemberName] string stamp = "")
    {
        var paths = FlintPaths.FromRoots(NewTempDir(stamp), NewTempDir(stamp), NewTempDir(stamp),
            NewTempDir(stamp), NewTempDir(stamp));
        var fixture = new TestFlintPaths(paths, null);
        fixture._created.AddRange(
        [
            paths.ConfigRoot, paths.DataRoot, paths.StoreRoot, paths.CacheRoot, paths.StateRoot,
        ]);
        return fixture;
    }

    private static string NewTempDir(string stamp) =>
        Path.Combine(Path.GetTempPath(), $"flint-test-{stamp}-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var dir in _created)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
